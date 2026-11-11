using System.Runtime.InteropServices;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

public sealed unsafe partial class PdfDocument
{
    private static int[] Validate(IEnumerable<int> indices, int count, bool allowEmpty = false)
    {
        var arr = indices.Distinct().OrderBy(i => i).ToArray();
        if (!allowEmpty && arr.Length == 0) throw new ArgumentException("Hiç sayfa seçilmedi.");
        foreach (var i in arr)
            if ((uint)i >= (uint)count) throw new PdfException(PdfErrorKind.Page, $"Sayfa {i + 1} yok (toplam {count}).");
        return arr;
    }

    /// <summary>Kayıtlı yer imi modelinin sayfa numaralarını bir eşleme ile günceller (değiştirilmişse).</summary>
    private void RemapBookmarks(Func<int, int> map)
    {
        if (!_bookmarksDirty || _bookmarks is null) { _bookmarks = null; return; }
        void Walk(List<Bookmark> list) { foreach (var b in list) { if (b.PageIndex >= 0) b.PageIndex = map(b.PageIndex); Walk(b.Children); } }
        Walk(_bookmarks);
    }

    public Task DeletePagesAsync(IEnumerable<int> indices) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        if (arr.Length >= PageCount) throw new PdfException(PdfErrorKind.Unsupported, "Belgenin tüm sayfaları silinemez; en az bir sayfa kalmalı.");
        ClosePages();
        for (int k = arr.Length - 1; k >= 0; k--) Native.FPDFPage_Delete(_doc, arr[k]);
        var set = arr.ToHashSet();
        RemapBookmarks(old => set.Contains(old) ? -1 : old - arr.Count(d => d < old));
        MarkChanged();
    });

    /// <summary>
    /// Sayfaları taşır. <paramref name="insertBefore"/>: özgün belgedeki hedef konum (0..PageCount);
    /// seçili sayfalar bu sayfanın önüne, kendi sıralarıyla yerleştirilir.
    /// </summary>
    public Task MovePagesAsync(IEnumerable<int> indices, int insertBefore) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        var order = PageOrder.ComputeMove(PageCount, arr, insertBefore);
        if (order.SequenceEqual(Enumerable.Range(0, PageCount))) return;
        int dest = PageOrder.MoveDestination(arr, insertBefore);
        ClosePages();
        fixed (int* p = arr)
            if (Native.FPDF_MovePages(_doc, p, new CULong((uint)arr.Length), dest) == 0)
                throw new PdfException(PdfErrorKind.Unsupported, "Sayfalar taşınamadı.");
        var inverse = new int[order.Length];
        for (int n = 0; n < order.Length; n++) inverse[order[n]] = n;
        RemapBookmarks(old => inverse[old]);
        MarkChanged();
    });

    /// <summary>Sayfaları kalıcı olarak döndürür (90° katları, saat yönünde pozitif).</summary>
    public Task RotatePagesAsync(IEnumerable<int> indices, int quarterTurns) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        foreach (var i in arr)
        {
            var page = Page(i);
            int r = Native.FPDFPage_GetRotation(page);
            Native.FPDFPage_SetRotation(page, (((r + quarterTurns) % 4) + 4) % 4);
            ClosePage(i);
        }
        MarkChanged();
    });

    public Task InsertBlankPagesAsync(int at, int count, SizeF size) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        at = Math.Clamp(at, 0, PageCount);
        if (count <= 0) return;
        ClosePages();
        for (int k = 0; k < count; k++)
        {
            var p = Native.FPDFPage_New(_doc, at + k, size.Width, size.Height);
            if (p == 0) throw new PdfException(PdfErrorKind.Unknown, "Boş sayfa eklenemedi.");
            Native.FPDF_ClosePage(p);
        }
        RemapBookmarks(old => old >= at ? old + count : old);
        MarkChanged();
    });

    /// <summary>Başka bir açık belgeden sayfaları içe aktarır.</summary>
    public Task ImportPagesAsync(PdfDocument source, IEnumerable<int>? indices, int at) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        if (ReferenceEquals(source, this)) { DuplicateCore(indices?.ToArray() ?? Enumerable.Range(0, PageCount).ToArray(), at); return; }
        source.ThrowIfDisposed();
        var arr = indices is null ? Enumerable.Range(0, source.PageCount).ToArray() : indices.ToArray();
        foreach (var i in arr) if ((uint)i >= (uint)source.PageCount) throw new PdfException(PdfErrorKind.Page, $"Kaynakta sayfa {i + 1} yok.");
        ImportCore(source._doc, arr, at);
    });

    private void ImportCore(nint srcDoc, int[] arr, int at)
    {
        at = Math.Clamp(at, 0, PageCount);
        ClosePages();
        fixed (int* p = arr)
            if (Native.FPDF_ImportPagesByIndex(_doc, srcDoc, p, new CULong((uint)arr.Length), at) == 0)
                throw new PdfException(PdfErrorKind.Unknown, "Sayfalar içe aktarılamadı.");
        int n = arr.Length;
        RemapBookmarks(old => old >= at ? old + n : old);
        MarkChanged();
    }


    /// <summary>Seçili sayfaların kopyalarını, her sayfanın hemen arkasına ekler.</summary>
    public Task DuplicatePagesAsync(IEnumerable<int> indices) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        WithSelfCopy(copy =>
        {
            for (int k = arr.Length - 1; k >= 0; k--)
            {
                int i = arr[k];
                var one = new[] { i };
                ClosePages();
                fixed (int* p = one)
                    if (Native.FPDF_ImportPagesByIndex(_doc, copy, p, new CULong(1u), i + 1) == 0)
                        throw new PdfException(PdfErrorKind.Unknown, "Sayfa kopyalanamadı.");
                RemapBookmarks(old => old > i ? old + 1 : old);
            }
        });
        MarkChanged();
    });

    private void DuplicateCore(int[] arr, int at)
    {
        WithSelfCopy(copy => ImportCore(copy, arr, at));
    }

    /// <summary>Belgenin bellekteki bir kopyasını açıp işleme verir (kendinden sayfa içe aktarmak için). Motor iş parçacığında.</summary>
    private void WithSelfCopy(Action<nint> work)
    {
        var ms = new MemoryStream();
        if (!StreamWriterBridge.Save(_doc, ms, Native.FPDF_NO_INCREMENTAL)) throw new PdfException(PdfErrorKind.Save, "Geçici kopya oluşturulamadı.");
        var data = ms.ToArray();
        var pw = NativeStrings.Utf8Z(Password);
        // Belge kapanana dek tampon sabit kalmalı: yerel belleğe kopyalanır.
        nint mem = (nint)NativeMemory.Alloc((nuint)data.Length);
        data.AsSpan().CopyTo(new Span<byte>((void*)mem, data.Length));
        nint d;
        fixed (byte* p = pw) d = Native.FPDF_LoadMemDocument64((void*)mem, (nuint)data.Length, Password is null ? null : p);
        if (d == 0) { NativeMemory.Free((void*)mem); throw new PdfException(PdfErrorKind.Unknown, "Geçici kopya açılamadı."); }
        try { work(d); }
        finally { Native.FPDF_CloseDocument(d); NativeMemory.Free((void*)mem); }
    }

    /// <summary>Sayfaları kırpar (CropBox). Kenar boşlukları görüntülenen yöne göredir.</summary>
    public Task CropPagesAsync(IEnumerable<int> indices, Margins m) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        foreach (var i in arr)
        {
            var page = Page(i);
            var box = GetEffectiveBox(page);
            int rot = Native.FPDFPage_GetRotation(page);
            // Görüntülenen kenarları döndürülmemiş kutunun kenarlarına eşle.
            (double l, double t, double r, double b) = rot switch
            {
                1 => (m.Top, m.Right, m.Bottom, m.Left),
                2 => (m.Right, m.Bottom, m.Left, m.Top),
                3 => (m.Bottom, m.Left, m.Top, m.Right),
                _ => (m.Left, m.Top, m.Right, m.Bottom),
            };
            var nb = new RectF(box.Left + l, box.Bottom + b, box.Right - r, box.Top - t);
            if (nb.Width < 10 || nb.Height < 10) throw new PdfException(PdfErrorKind.Unsupported, $"Sayfa {i + 1} için kırpma payı sayfadan büyük.");
            Native.FPDFPage_SetCropBox(page, (float)nb.Left, (float)nb.Bottom, (float)nb.Right, (float)nb.Top);
            ClosePage(i);
        }
        MarkChanged();
    });

    /// <summary>Sayfa boyutunu değiştirir; içerik ölçeklenip ortalanır (orantı korunur).</summary>
    public Task ResizePagesAsync(IEnumerable<int> indices, SizeF target, bool keepOrientation = true) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = Validate(indices, PageCount);
        foreach (var i in arr)
        {
            var page = Page(i);
            var box = GetEffectiveBox(page);
            int rot = Native.FPDFPage_GetRotation(page);
            double tw = target.Width, th = target.Height;
            // Hedef görüntülenen yöndedir; döndürülmüş sayfada kutu yönüne çevir.
            if (rot % 2 == 1) (tw, th) = (th, tw);
            if (keepOrientation && (box.Width > box.Height) != (tw > th)) (tw, th) = (th, tw);
            double s = Math.Min(tw / box.Width, th / box.Height);
            double ox = (tw - box.Width * s) / 2 - box.Left * s;
            double oy = (th - box.Height * s) / 2 - box.Bottom * s;
            var mtx = new FS_MATRIX { a = (float)s, b = 0, c = 0, d = (float)s, e = (float)ox, f = (float)oy };
            var clip = new FS_RECTF { left = 0, bottom = 0, right = (float)tw, top = (float)th };
            if (Native.FPDFPage_TransFormWithClip(page, &mtx, &clip) == 0)
                throw new PdfException(PdfErrorKind.Unsupported, $"Sayfa {i + 1} ölçeklenemedi.");
            Native.FPDFPage_SetMediaBox(page, 0, 0, (float)tw, (float)th);
            Native.FPDFPage_SetCropBox(page, 0, 0, (float)tw, (float)th);
            ClosePage(i);
        }
        MarkChanged();
    });

    private static RectF GetEffectiveBox(nint page)
    {
        if (Native.FPDFPage_GetCropBox(page, out var l, out var b, out var r, out var t) != 0)
        {
            var c = new RectF(l, b, r, t).Normalize();
            if (!c.IsEmpty) return c;
        }
        if (Native.FPDFPage_GetMediaBox(page, out l, out b, out r, out t) != 0)
        {
            var mb = new RectF(l, b, r, t).Normalize();
            if (!mb.IsEmpty) return mb;
        }
        return new RectF(0, 0, Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page));
    }

    /// <summary>Seçili sayfalardan yeni (kaydedilmemiş) bir belge oluşturur.</summary>
    public Task<PdfDocument> ExtractAsync(IEnumerable<int> indices) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var arr = indices.ToArray();
        foreach (var i in arr) if ((uint)i >= (uint)PageCount) throw new PdfException(PdfErrorKind.Page, $"Sayfa {i + 1} yok.");
        if (arr.Length == 0) throw new ArgumentException("Hiç sayfa seçilmedi.");
        var nd = new PdfDocument(_engine) { _doc = Native.FPDF_CreateNewDocument() };
        fixed (int* p = arr)
            if (Native.FPDF_ImportPagesByIndex(nd._doc, _doc, p, new CULong((uint)arr.Length), 0) == 0)
            {
                nd.CloseNative();
                throw new PdfException(PdfErrorKind.Unknown, "Sayfalar ayıklanamadı.");
            }
        Native.FPDF_CopyViewerPreferences(nd._doc, _doc);
        nd.RefreshStructure();
        nd.IsModified = true;
        nd._bookmarks = new();
        return nd;
    });
}

/// <summary>Sayfa sırası hesapları (arayüzden bağımsız, test edilebilir).</summary>
public static class PageOrder
{
    /// <summary>FPDF_MovePages için hedef indeks: taşınan sayfalar çıkarıldıktan sonraki konum.</summary>
    public static int MoveDestination(IReadOnlyList<int> sortedSelection, int insertBefore)
        => insertBefore - sortedSelection.Count(i => i < insertBefore);

    /// <summary>Taşıma sonrası yeni sıra: result[yeniKonum] = eskiIndeks.</summary>
    public static int[] ComputeMove(int count, IReadOnlyList<int> sortedSelection, int insertBefore)
    {
        insertBefore = Math.Clamp(insertBefore, 0, count);
        var set = sortedSelection.ToHashSet();
        var rest = Enumerable.Range(0, count).Where(i => !set.Contains(i)).ToList();
        int dest = MoveDestination(sortedSelection, insertBefore);
        rest.InsertRange(dest, sortedSelection);
        return rest.ToArray();
    }
}
