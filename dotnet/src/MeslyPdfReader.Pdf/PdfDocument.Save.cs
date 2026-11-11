using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

public sealed unsafe partial class PdfDocument
{
    private const uint SaveFlags = Native.FPDF_NO_INCREMENTAL | Native.FPDF_SUBSET_NEW_FONTS;

    /// <summary>
    /// Belgeyi güvenli biçimde kaydeder: önce aynı klasörde geçici dosyaya yazılır, yeniden açılarak
    /// doğrulanır, sonra hedefin yerine taşınır. Hata olursa özgün dosya hiç değişmez.
    /// Kayıttan sonra belge, kaydedilen dosyadan yeniden okunur.
    /// </summary>
    public Task SaveAsync(string path, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.mesly-tmp");
        try
        {
            WriteCopyCore(tmp);
            ct.ThrowIfCancellationRequested();
            VerifyFile(tmp, PageCount);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        bool replacingSource = _sourcePath is not null && PathsEqual(_sourcePath, path);
        if (!replacingSource)
        {
            // Hedef, okunan dosya değil: belge kapatılmadan taşınır; hata olursa belge olduğu gibi kalır.
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception ex)
            {
                TryDelete(tmp);
                throw SaveError(ex);
            }
            CloseNative();
        }
        else
        {
            // Okunan dosyanın üzerine yazılıyor: önce kapatılmalı (Windows dosya kilidi).
            var bookmarks = _bookmarks; var dirty = _bookmarksDirty;
            CloseNative();
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception ex)
            {
                // Değişiklikler kaybolmasın: belge, değişiklikleri içeren geçici dosyadan açık kalır.
                LoadFromFile(tmp, Password);
                _ownedTempSources.Add(tmp);
                _bookmarks = bookmarks; _bookmarksDirty = dirty;
                IsModified = true;
                throw SaveError(ex);
            }
        }
        LoadFromFile(path, Password);
        FilePath = path;
        IsModified = false;
        _bookmarks = null; _bookmarksDirty = false;
        DeleteOwnedTempSources();
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }, ct);

    /// <summary>Belgenin o anki halini başka bir dosyaya yazar; açık belge değişmez (kurtarma, dışa aktarma için).</summary>
    public Task SaveCopyAsync(string path) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var tmp = path + ".mesly-tmp";
        try
        {
            WriteCopyCore(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch { TryDelete(tmp); throw; }
    });

    public Task<byte[]> SaveToBytesAsync() => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var tmp = Path.Combine(Path.GetTempPath(), $"mesly-{Guid.NewGuid():N}.pdf");
        try { WriteCopyCore(tmp); return File.ReadAllBytes(tmp); }
        finally { TryDelete(tmp); }
    });

    private void WriteCopyCore(string target)
    {
        // Sayfalarda bekleyen içerik değişiklikleri sayfalar kapatılınca belgeye işlenmiş olur.
        ClosePages();
        using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            if (!StreamWriterBridge.Save(_doc, fs, SaveFlags))
                throw new PdfException(PdfErrorKind.Save, "PDF motoru belgeyi yazamadı.");
            fs.Flush(true);
        }
        if (_bookmarksDirty && _bookmarks is not null)
            BookmarkWriter.Write(target, Password, _bookmarks);
    }

    private static void VerifyFile(string path, int expectedPages)
    {
        // Yazılan dosyayı bağımsız olarak aç ve sayfa sayısını doğrula.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var src = new StreamSource(fs, ownsStream: false);
        var d = Native.FPDF_LoadCustomDocument(src.Access, null);
        if (d == 0)
        {
            // Şifreli belgelerde parola gerekir; bu bir doğrulama hatası değildir.
            if ((int)Native.FPDF_GetLastError().Value == Native.FPDF_ERR_PASSWORD) return;
            throw new PdfException(PdfErrorKind.Save, "Yazılan dosya doğrulanamadı.");
        }
        try
        {
            int n = Native.FPDF_GetPageCount(d);
            if (n != expectedPages) throw new PdfException(PdfErrorKind.Save, $"Doğrulama hatası: {expectedPages} sayfa beklenirken {n} sayfa yazıldı.");
        }
        finally { Native.FPDF_CloseDocument(d); }
    }

    private static PdfException SaveError(Exception ex) => new(PdfErrorKind.Save,
        ex is UnauthorizedAccessException or IOException ? "Dosya başka bir program tarafından kullanılıyor, hedef geçersiz ya da yazma izni yok." : ex.Message, ex);

    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private readonly List<string> _ownedTempSources = new();

    private void DeleteOwnedTempSources()
    {
        foreach (var f in _ownedTempSources.ToArray())
            if (_sourcePath is null || !PathsEqual(f, _sourcePath)) { TryDelete(f); _ownedTempSources.Remove(f); }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // ───────────────────────────── Geri al / yinele için anlık görüntüler ─────────────────────────────

    /// <summary>Belgenin o anki halini klasöre yazar (geri alma noktası).</summary>
    public Task<DocumentSnapshot> CreateSnapshotAsync(string directory) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"snap-{Guid.NewGuid():N}.pdf");
        ClosePages();
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            if (!StreamWriterBridge.Save(_doc, fs, SaveFlags)) throw new PdfException(PdfErrorKind.Save, "Geri alma noktası oluşturulamadı.");
        return new DocumentSnapshot(file, _bookmarks?.Select(b => b.Clone()).ToList(), _bookmarksDirty, PageCount);
    });

    /// <summary>Belgeyi bir anlık görüntüye döndürür. Kaydetme hedefi (FilePath) değişmez.</summary>
    public Task RestoreSnapshotAsync(DocumentSnapshot snap, bool modified = true) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var pw = Password;
        CloseNative();
        LoadFromFile(snap.FilePath, pw);
        _bookmarks = snap.Bookmarks?.Select(b => b.Clone()).ToList();
        _bookmarksDirty = snap.BookmarksDirty;
        IsModified = modified;
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    });
}

/// <summary>Yer imi (outline) ağacını PDF'e yazar. PDFium outline yazamadığı için PDFsharp (MIT) kullanılır.</summary>
internal static class BookmarkWriter
{
    public static void Write(string path, string? password, IReadOnlyList<Bookmark> items)
    {
        var tmp = path + ".bm";
        try
        {
            using (var doc = password is null
                ? PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify)
                : PdfSharp.Pdf.IO.PdfReader.Open(path, password, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                doc.Outlines.Clear();
                void Add(PdfSharp.Pdf.PdfOutlineCollection into, Bookmark b)
                {
                    var page = b.PageIndex >= 0 && b.PageIndex < doc.PageCount ? doc.Pages[b.PageIndex] : null;
                    PdfSharp.Pdf.PdfOutline o;
                    if (page is not null) o = into.Add(b.Title, page, b.Children.Count > 0);
                    else
                    {
                        o = new PdfSharp.Pdf.PdfOutline { Title = b.Title, Opened = b.Children.Count > 0 };
                        into.Add(o);
                        if (!string.IsNullOrWhiteSpace(b.Uri))
                        {
                            var a = new PdfSharp.Pdf.PdfDictionary(doc);
                            a.Elements["/S"] = new PdfSharp.Pdf.PdfName("/URI");
                            a.Elements["/URI"] = new PdfSharp.Pdf.PdfString(b.Uri);
                            o.Elements["/A"] = a;
                        }
                    }
                    foreach (var c in b.Children) Add(o.Outlines, c);
                }
                foreach (var b in items) Add(doc.Outlines, b);
                if (items.Count > 0) doc.PageMode = PdfSharp.Pdf.PdfPageMode.UseOutlines;
                doc.Save(tmp);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not PdfException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw new PdfException(PdfErrorKind.Save, "Yer imleri yazılamadı: " + ex.Message, ex);
        }
    }
}
