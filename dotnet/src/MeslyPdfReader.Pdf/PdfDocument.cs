using System.Runtime.InteropServices;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

public sealed record RenderRequest(
    int FullWidth, int FullHeight,
    int X = 0, int Y = 0, int Width = 0, int Height = 0,
    int QuarterTurns = 0, bool Annotations = true, bool Printing = false, bool Grayscale = false);

/// <summary>Kenar boşlukları (PDF noktası), görüntülenen yönde.</summary>
public readonly record struct Margins(double Left, double Top, double Right, double Bottom)
{
    public static Margins FromMm(double l, double t, double r, double b) =>
        new(PaperSize.MmToPt(l), PaperSize.MmToPt(t), PaperSize.MmToPt(r), PaperSize.MmToPt(b));
}

public sealed record DocumentSnapshot(string FilePath, List<Bookmark>? Bookmarks, bool BookmarksDirty, int PageCount);

/// <summary>
/// Açık bir PDF belgesi. Tüm PDFium erişimi <see cref="PdfEngine"/> iş parçacığında yapılır;
/// genel yöntemler iş parçacığı güvenlidir ve Task döndürür.
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private const int MaxCachedPages = 24;
    private const long MaxRenderPixels = 120_000_000;

    private readonly PdfEngine _engine;
    private nint _doc;
    private StreamSource? _source;
    private string? _sourcePath;
    private SizeF[] _sizes = Array.Empty<SizeF>();
    private readonly LinkedList<(int Index, nint Page, nint Text)> _pageLru = new();
    private readonly Dictionary<int, LinkedListNode<(int Index, nint Page, nint Text)>> _pageMap = new();
    private readonly Dictionary<string, nint> _fonts = new();
    private List<Bookmark>? _bookmarks;
    private bool _bookmarksDirty;
    private bool _disposed;

    /// <summary>Kaydetme hedefi. Yeni / birleştirilmiş belgelerde null'dır.</summary>
    public string? FilePath { get; private set; }
    public string? Password { get; private set; }
    public int PageCount { get; private set; }
    /// <summary>İçerik her değiştiğinde artar; önbellek anahtarlarında kullanılır.</summary>
    public int Revision { get; private set; }
    public bool IsModified { get; private set; }
    public bool IsEncrypted { get; private set; }
    public long SourceLength { get; private set; }
    public string DisplayName => FilePath is null ? "Adsız.pdf" : Path.GetFileName(FilePath);

    /// <summary>Belgenin yapısı değiştiğinde (sayfa eklendi/silindi/döndürüldü...). Motor iş parçacığından çağrılır.</summary>
    public event EventHandler? Changed;

    private PdfDocument(PdfEngine engine) => _engine = engine;

    // ───────────────────────────── Açma / oluşturma ─────────────────────────────

    public static Task<PdfDocument> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        var engine = PdfEngine.Instance;
        return engine.RunAsync(() =>
        {
            var d = new PdfDocument(engine);
            d.LoadFromFile(path, password);
            d.FilePath = Path.GetFullPath(path);
            return d;
        }, ct);
    }

    public static Task<PdfDocument> OpenBytesAsync(byte[] data, string? password = null, string? filePath = null)
    {
        var engine = PdfEngine.Instance;
        return engine.RunAsync(() =>
        {
            var d = new PdfDocument(engine);
            d.LoadFromStream(new MemoryStream(data, writable: false), password, null);
            d.FilePath = filePath;
            return d;
        });
    }

    /// <summary>Boş yeni belge. <paramref name="pages"/> kadar verilen boyutta boş sayfa ekler.</summary>
    public static Task<PdfDocument> CreateAsync(SizeF? pageSize = null, int pages = 0)
    {
        var engine = PdfEngine.Instance;
        return engine.RunAsync(() =>
        {
            var d = new PdfDocument(engine) { _doc = Native.FPDF_CreateNewDocument() };
            if (d._doc == 0) throw new PdfException(PdfErrorKind.Unknown, "Yeni belge oluşturulamadı.");
            var size = pageSize ?? PaperSize.A4.ToPoints();
            for (int i = 0; i < pages; i++)
            {
                var p = Native.FPDFPage_New(d._doc, i, size.Width, size.Height);
                if (p != 0) Native.FPDF_ClosePage(p);
            }
            d.RefreshStructure();
            d.IsModified = pages > 0;
            d._bookmarks = new();
            return d;
        });
    }

    private void LoadFromFile(string path, string? password)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        }
        catch (FileNotFoundException ex) { throw new PdfException(PdfErrorKind.File, $"({Path.GetFileName(path)})", ex); }
        catch (DirectoryNotFoundException ex) { throw new PdfException(PdfErrorKind.File, $"({Path.GetFileName(path)})", ex); }
        catch (UnauthorizedAccessException ex) { throw new PdfException(PdfErrorKind.File, "Dosyaya erişim izni yok.", ex); }
        catch (IOException ex) { throw new PdfException(PdfErrorKind.File, ex.Message, ex); }
        LoadFromStream(fs, password, path);
    }

    private void LoadFromStream(Stream stream, string? password, string? sourcePath)
    {
        if (stream.Length < 8) { stream.Dispose(); throw new PdfException(PdfErrorKind.Format, "Dosya boş veya çok küçük."); }
        var src = new StreamSource(stream);
        var pw = NativeStrings.Utf8Z(password);
        nint doc;
        fixed (byte* p = pw) doc = Native.FPDF_LoadCustomDocument(src.Access, password is null ? null : p);
        if (doc == 0)
        {
            var err = (int)Native.FPDF_GetLastError().Value;
            src.Dispose();
            throw new PdfException(err switch
            {
                Native.FPDF_ERR_FILE => PdfErrorKind.File,
                Native.FPDF_ERR_FORMAT => PdfErrorKind.Format,
                Native.FPDF_ERR_PASSWORD => PdfErrorKind.Password,
                Native.FPDF_ERR_SECURITY => PdfErrorKind.Security,
                Native.FPDF_ERR_PAGE => PdfErrorKind.Page,
                _ => PdfErrorKind.Unknown,
            });
        }
        _doc = doc;
        _source = src;
        _sourcePath = sourcePath;
        SourceLength = src.Length;
        Password = password;
        IsEncrypted = Native.FPDF_GetSecurityHandlerRevision(doc) != -1;
        RefreshStructure();
        if (PageCount == 0)
        {
            CloseNative();
            throw new PdfException(PdfErrorKind.Format, "Belgede görüntülenebilir sayfa yok.");
        }
    }

    // ───────────────────────────── Sayfa önbelleği ─────────────────────────────

    private void ThrowIfDisposed() { if (_disposed || _doc == 0) throw new ObjectDisposedException(nameof(PdfDocument), "Belge kapatıldı."); }

    private nint Page(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)PageCount) throw new PdfException(PdfErrorKind.Page, $"Sayfa {index + 1} yok (toplam {PageCount}).");
        if (_pageMap.TryGetValue(index, out var node))
        {
            _pageLru.Remove(node); _pageLru.AddFirst(node);
            return node.Value.Page;
        }
        var page = Native.FPDF_LoadPage(_doc, index);
        if (page == 0) throw new PdfException(PdfErrorKind.Page, $"Sayfa {index + 1} yüklenemedi.");
        var n = _pageLru.AddFirst((index, page, (nint)0));
        _pageMap[index] = n;
        while (_pageLru.Count > MaxCachedPages)
        {
            var last = _pageLru.Last!;
            ClosePageEntry(last.Value);
            _pageMap.Remove(last.Value.Index);
            _pageLru.RemoveLast();
        }
        return page;
    }

    private nint TextPage(int index)
    {
        Page(index);
        var node = _pageMap[index];
        if (node.Value.Text != 0) return node.Value.Text;
        var tp = Native.FPDFText_LoadPage(node.Value.Page);
        if (tp == 0) throw new PdfException(PdfErrorKind.Page, $"Sayfa {index + 1} metni okunamadı.");
        node.Value = (node.Value.Index, node.Value.Page, tp);
        return tp;
    }

    private static void ClosePageEntry((int Index, nint Page, nint Text) e)
    {
        if (e.Text != 0) Native.FPDFText_ClosePage(e.Text);
        if (e.Page != 0) Native.FPDF_ClosePage(e.Page);
    }

    private void ClosePages()
    {
        foreach (var e in _pageLru) ClosePageEntry(e);
        _pageLru.Clear(); _pageMap.Clear();
    }

    private void ClosePage(int index)
    {
        if (_pageMap.Remove(index, out var node)) { ClosePageEntry(node.Value); _pageLru.Remove(node); }
    }

    private void RefreshStructure()
    {
        PageCount = Native.FPDF_GetPageCount(_doc);
        var sizes = new SizeF[PageCount];
        for (int i = 0; i < PageCount; i++)
            sizes[i] = Native.FPDF_GetPageSizeByIndexF(_doc, i, out var s) != 0 ? new SizeF(s.width, s.height) : new SizeF(612, 792);
        _sizes = sizes;
    }

    private void MarkChanged(bool structure = true)
    {
        if (structure) { ClosePages(); RefreshStructure(); }
        Revision++;
        IsModified = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Sorgular ─────────────────────────────

    /// <summary>Görüntülenen (döndürme uygulanmış) sayfa boyutu, PDF noktası. Önbellekten, kilitsiz okunur.</summary>
    public SizeF GetPageSize(int index)
    {
        var s = _sizes;
        return (uint)index < (uint)s.Length ? s[index] : new SizeF(612, 792);
    }

    public IReadOnlyList<SizeF> PageSizes => _sizes;

    public Task<int> GetRotationAsync(int index) => _engine.RunAsync(() => Native.FPDFPage_GetRotation(Page(index)));

    public Task<BgraImage> RenderAsync(int index, RenderRequest r, CancellationToken ct = default, bool lowPriority = false)
        => _engine.RunAsync(() => RenderCore(index, r, ct), ct, lowPriority);

    /// <summary>Motor iş parçacığında çalışan senkron çizim (yazdırma sayfalayıcısı gibi senkron çağıranlar için).</summary>
    public BgraImage Render(int index, RenderRequest r) => _engine.Invoke(() => RenderCore(index, r, default));

    private BgraImage RenderCore(int index, RenderRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int w = r.Width > 0 ? r.Width : r.FullWidth;
        int h = r.Height > 0 ? r.Height : r.FullHeight;
        if (w <= 0 || h <= 0) throw new ArgumentException("Çizim boyutu geçersiz.");
        if ((long)w * h > MaxRenderPixels) throw new PdfException(PdfErrorKind.Unsupported, "İstenen çizim boyutu çok büyük.");
        var page = Page(index);
        int stride = w * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(stride * h);
        int flags = Native.FPDF_RENDER_LIMITEDIMAGECACHE;
        if (r.Annotations) flags |= Native.FPDF_ANNOT;
        if (r.Printing) flags |= Native.FPDF_PRINTING;
        if (r.Grayscale) flags |= Native.FPDF_GRAYSCALE;
        fixed (byte* p = pixels)
        {
            var bmp = Native.FPDFBitmap_CreateEx(w, h, Native.FPDFBitmap_BGRA, p, stride);
            if (bmp == 0) throw new OutOfMemoryException("Sayfa görüntüsü için bellek ayrılamadı.");
            try
            {
                Native.FPDFBitmap_FillRect(bmp, 0, 0, w, h, new CULong(0xFFFFFFFF));
                Native.FPDF_RenderPageBitmap(bmp, page, -r.X, -r.Y, r.FullWidth, r.FullHeight, ((r.QuarterTurns % 4) + 4) % 4, flags);
            }
            finally { Native.FPDFBitmap_Destroy(bmp); }
        }
        return new BgraImage { Width = w, Height = h, Stride = stride, Pixels = pixels };
    }

    public Task<PageText> GetPageTextAsync(int index, CancellationToken ct = default, bool lowPriority = false)
        => _engine.RunAsync(() => BuildPageText(index), ct, lowPriority);

    private PageText BuildPageText(int index)
    {
        var tp = TextPage(index);
        int n = Math.Max(0, Native.FPDFText_CountChars(tp));
        var chars = new char[n];
        var boxes = new RectF[n];
        for (int k = 0; k < n; k++)
        {
            uint u = Native.FPDFText_GetUnicode(tp, k);
            chars[k] = u switch
            {
                0 => ' ',
                0x02 or 0xFFFE => '-',
                <= 0xFFFF => (char)u,
                _ => '�',
            };
            if (Native.FPDFText_IsGenerated(tp, k) == 1) continue;
            if (Native.FPDFText_GetLooseCharBox(tp, k, out var rc) != 0)
            {
                var box = new RectF(rc.left, rc.bottom, rc.right, rc.top).Normalize();
                if (!box.IsEmpty) boxes[k] = box;
            }
        }
        return new PageText { PageIndex = index, Text = new string(chars), Boxes = boxes };
    }


    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");


    public Task<IReadOnlyList<LinkInfo>> GetLinksAsync(int index, CancellationToken ct = default)
        => _engine.RunAsync(() =>
        {
            var page = Page(index);
            var list = new List<LinkInfo>();
            int pos = 0;
            while (list.Count < 2000 && Native.FPDFLink_Enumerate(page, ref pos, out var link) != 0)
            {
                if (Native.FPDFLink_GetAnnotRect(link, out var rc) == 0) continue;
                var rect = new RectF(rc.left, rc.bottom, rc.right, rc.top).Normalize();
                int target = -1; string? uri = null;
                var dest = Native.FPDFLink_GetDest(_doc, link);
                if (dest != 0) target = Native.FPDFDest_GetDestPageIndex(_doc, dest);
                else
                {
                    var action = Native.FPDFLink_GetAction(link);
                    if (action != 0) (target, uri) = ReadAction(action);
                }
                if (target >= 0 || uri is not null) list.Add(new LinkInfo(rect, target, uri));
            }
            return (IReadOnlyList<LinkInfo>)list;
        }, ct);

    private (int Page, string? Uri) ReadAction(nint action)
    {
        uint type = (uint)Native.FPDFAction_GetType(action).Value;
        if (type == Native.PDFACTION_GOTO)
        {
            var d = Native.FPDFAction_GetDest(_doc, action);
            return (d != 0 ? Native.FPDFDest_GetDestPageIndex(_doc, d) : -1, null);
        }
        if (type == Native.PDFACTION_URI)
        {
            var doc = _doc;
            var uri = NativeStrings.ReadAscii((buf, len) => (uint)Native.FPDFAction_GetURIPath(doc, action, (void*)buf, new CULong(len)).Value);
            return (-1, string.IsNullOrWhiteSpace(uri) ? null : uri.Trim());
        }
        return (-1, null);
    }

    public Task<List<Bookmark>> GetBookmarksAsync() => _engine.RunAsync(() =>
    {
        _bookmarks ??= ReadBookmarks();
        return _bookmarks.Select(b => b.Clone()).ToList();
    });

    public bool BookmarksModified => _bookmarksDirty;

    /// <summary>Yer imi ağacını değiştirir; kaydetmede PDF'e yazılır.</summary>
    public Task SetBookmarksAsync(IEnumerable<Bookmark> items) => _engine.RunAsync(() =>
    {
        _bookmarks = items.Select(b => b.Clone()).ToList();
        _bookmarksDirty = true;
        IsModified = true;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private List<Bookmark> ReadBookmarks()
    {
        var result = new List<Bookmark>();
        var visited = new HashSet<nint>();
        int budget = 20000;
        void Walk(nint parent, List<Bookmark> into, int depth)
        {
            if (depth > 64) return;
            var child = Native.FPDFBookmark_GetFirstChild(_doc, parent);
            while (child != 0 && budget-- > 0 && visited.Add(child))
            {
                var c = child;
                var title = NativeStrings.ReadUtf16((buf, len) => (uint)Native.FPDFBookmark_GetTitle(c, (void*)buf, new CULong(len)).Value);
                var bm = new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? "(adsız)" : title.Trim() };
                var dest = Native.FPDFBookmark_GetDest(_doc, child);
                if (dest != 0) bm.PageIndex = Native.FPDFDest_GetDestPageIndex(_doc, dest);
                else
                {
                    var act = Native.FPDFBookmark_GetAction(child);
                    if (act != 0) (bm.PageIndex, bm.Uri) = ReadAction(act);
                }
                into.Add(bm);
                Walk(child, bm.Children, depth + 1);
                child = Native.FPDFBookmark_GetNextSibling(_doc, child);
            }
        }
        Walk(0, result, 0);
        return result;
    }

    public Task<DocumentInfo> GetInfoAsync() => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        string Meta(string tag)
        {
            var t = NativeStrings.Utf8Z(tag);
            var doc = _doc;
            fixed (byte* tp = t)
            {
                var tptr = (nint)tp;
                return NativeStrings.ReadUtf16((buf, len) => (uint)Native.FPDF_GetMetaText(doc, (byte*)tptr, (void*)buf, new CULong(len)).Value);
            }
        }
        Native.FPDF_GetFileVersion(_doc, out int ver);
        return new DocumentInfo
        {
            Title = Meta("Title"), Author = Meta("Author"), Subject = Meta("Subject"), Keywords = Meta("Keywords"),
            Creator = Meta("Creator"), Producer = Meta("Producer"),
            CreationDate = FormatPdfDate(Meta("CreationDate")), ModDate = FormatPdfDate(Meta("ModDate")),
            PdfVersion = ver, PageCount = PageCount, IsEncrypted = IsEncrypted,
            Permissions = (uint)Native.FPDF_GetDocPermissions(_doc).Value, FileSize = SourceLength,
        };
    });

    /// <summary>"D:20240131120000+03'00'" → "31.01.2024 12:00".</summary>
    public static string? FormatPdfDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.StartsWith("D:") ? s[2..] : s;
        if (t.Length >= 8 && int.TryParse(t[..4], out var y) && int.TryParse(t[4..6], out var mo) && int.TryParse(t[6..8], out var d))
        {
            int hh = t.Length >= 10 && int.TryParse(t[8..10], out var a) ? a : 0;
            int mm = t.Length >= 12 && int.TryParse(t[10..12], out var b) ? b : 0;
            try { return new DateTime(y, mo, d, hh, mm, 0).ToString("dd.MM.yyyy HH:mm", TextSearch.Turkish); } catch { }
        }
        return s;
    }

    // ───────────────────────────── Kapatma ─────────────────────────────

    private void CloseNative()
    {
        ClosePages();
        foreach (var f in _fonts.Values) Native.FPDFFont_Close(f);
        _fonts.Clear();
        if (_doc != 0) { Native.FPDF_CloseDocument(_doc); _doc = 0; }
        _source?.Dispose(); _source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { _engine.Invoke(CloseNative); } catch { }
        foreach (var f in _ownedTempSources) TryDelete(f);
        _disposed = true;
    }
}
