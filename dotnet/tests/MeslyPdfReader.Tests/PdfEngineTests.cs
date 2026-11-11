using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

public class OpenAndReadTests
{
    [Theory]
    [InlineData("links_outline.pdf", 5, 14)]
    [InlineData("v13.pdf", 1, 13)]
    [InlineData("v17_objstreams.pdf", 5, 17)]
    public async Task OpensDifferentVersions(string file, int pages, int version)
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F(file));
        Assert.Equal(pages, doc.PageCount);
        var info = await doc.GetInfoAsync();
        Assert.Equal(version, info.PdfVersion);
        Assert.False(info.IsEncrypted);
    }

    [Fact]
    public async Task ReadsMetadataInTurkish()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        var info = await doc.GetInfoAsync();
        Assert.Equal("Bağlantı ve Yer İmi Testi", info.Title);
        Assert.Equal("Mesly Test", info.Author);
    }

    [Fact]
    public async Task ExtractsTurkishCharactersExactly()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("v13.pdf"));
        var t = await doc.GetPageTextAsync(0);
        Assert.Contains("İĞÜŞÖÇ ığüşöç", t.Text);
        Assert.Equal(t.Text.Length, t.Boxes.Length);
        Assert.Contains(t.Boxes, b => !b.IsEmpty);
    }

    [Fact]
    public async Task ReadsBookmarksTree()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        var bms = await doc.GetBookmarksAsync();
        Assert.Equal(5, bms.Count);
        Assert.Equal("Bölüm 1 — Giriş", bms[0].Title);
        Assert.Equal(0, bms[0].PageIndex);
        Assert.Equal(3, bms[3].PageIndex);
        Assert.Single(bms[1].Children);
        Assert.Equal("Alt başlık ığüşöç", bms[1].Children[0].Title);
    }

    [Fact]
    public async Task ReadsInternalAndExternalLinks()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        var links = await doc.GetLinksAsync(0);
        Assert.Contains(links, l => l.Uri == "https://example.com/mesly");
        Assert.Contains(links, l => l.TargetPage == 3);
        var uri = links.First(l => l.Uri is not null);
        Assert.True(uri.Rect.Contains(100, 705));
    }

    [Theory]
    [InlineData("garbage.pdf", PdfErrorKind.Format)]
    [InlineData("notpdf.pdf", PdfErrorKind.Format)]
    [InlineData("yok-boyle-dosya.pdf", PdfErrorKind.File)]
    public async Task CorruptFilesGiveClearErrors(string file, PdfErrorKind kind)
    {
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(Fx.F(file)));
        Assert.Equal(kind, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task TruncatedFileIsRepairedOrRejectedWithoutCrash()
    {
        try
        {
            using var doc = await PdfDocument.OpenAsync(Fx.F("truncated.pdf"));
            Assert.True(doc.PageCount >= 1);
            var img = await doc.RenderAsync(0, new RenderRequest(200, 280));
            Assert.Equal(200, img.Width);
        }
        catch (PdfException ex) { Assert.Equal(PdfErrorKind.Format, ex.Kind); }
    }

    [Fact]
    public async Task EmptyFileIsRejected()
    {
        var dir = Fx.TempDir();
        var p = Path.Combine(dir, "bos.pdf");
        File.WriteAllBytes(p, Array.Empty<byte>());
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(p));
        Assert.Equal(PdfErrorKind.Format, ex.Kind);
    }

    [Theory]
    [InlineData("encrypted_aes256.pdf", "açık şifre")]
    [InlineData("encrypted_rc4.pdf", "1234")]
    public async Task EncryptedNeedsPassword(string file, string password)
    {
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(Fx.F(file)));
        Assert.Equal(PdfErrorKind.Password, ex.Kind);
        var wrong = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(Fx.F(file), "yanlış"));
        Assert.Equal(PdfErrorKind.Password, wrong.Kind);
        using var doc = await PdfDocument.OpenAsync(Fx.F(file), password);
        Assert.True(doc.IsEncrypted);
        Assert.Equal(5, doc.PageCount);
        Assert.Contains("Çalışma Şartları", (await doc.GetPageTextAsync(0)).Text);
    }

    [Fact]
    public async Task EncryptedDocumentStaysEncryptedAfterEditAndSave()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "sifreli.pdf");
        File.Copy(Fx.F("encrypted_aes256.pdf"), path);
        using (var doc = await PdfDocument.OpenAsync(path, "açık şifre"))
        {
            await doc.DeletePagesAsync(new[] { 4 });
            await doc.SaveAsync(path);
            Assert.Equal(4, doc.PageCount);
        }
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfDocument.OpenAsync(path));
        Assert.Equal(PdfErrorKind.Password, ex.Kind);
        using var again = await PdfDocument.OpenAsync(path, "açık şifre");
        Assert.Equal(4, again.PageCount);
    }
}

public class RenderTests
{
    [Fact]
    public async Task RendersPageWithInk()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        var size = doc.GetPageSize(0);
        Assert.InRange(size.Width, 594, 596);
        Assert.InRange(size.Height, 841, 843);
        var img = await doc.RenderAsync(0, new RenderRequest(595, 842));
        Assert.Equal(595, img.Width); Assert.Equal(842, img.Height);
        Assert.Equal(595 * 4, img.Stride);
        double ink = Fx.InkRatio(img);
        Assert.InRange(ink, 0.001, 0.2);
    }

    [Fact]
    public async Task RendersRegionAndRotation()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        var region = await doc.RenderAsync(0, new RenderRequest(1190, 1684, X: 100, Y: 100, Width: 300, Height: 200));
        Assert.Equal(300, region.Width); Assert.Equal(200, region.Height);
        var rotated = await doc.RenderAsync(0, new RenderRequest(842, 595, QuarterTurns: 1));
        Assert.Equal(842, rotated.Width);
        Assert.True(Fx.InkRatio(rotated) > 0.001);
    }

    [Fact]
    public async Task CancelledRenderIsSkipped()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doc.RenderAsync(0, new RenderRequest(100, 100), cts.Token));
    }

    [Fact]
    public async Task ParallelRequestsFromManyThreadsAreSafe()
    {
        using var a = await PdfDocument.OpenAsync(Fx.F("links_outline.pdf"));
        using var b = await PdfDocument.OpenAsync(Fx.F("v17_objstreams.pdf"));
        var tasks = Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            var d = i % 2 == 0 ? a : b;
            var img = await d.RenderAsync(i % 5, new RenderRequest(120 + i, 170 + i));
            var t = await d.GetPageTextAsync(i % 5);
            return img.Width == 120 + i && t.Text.Contains("Bölüm");
        })).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }
}

public class CreateEditSaveTests
{
    [Fact]
    public async Task WritesRealTextObjectsWithTurkishFont()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "turkce.pdf");
        using (var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1))
        {
            await doc.AddTextAsync(0, Fx.TurkishPangram, 50, 700, 14, Fx.Font);
            await doc.SaveAsync(path);
            Assert.False(doc.IsModified);
            Assert.Equal(path, doc.FilePath);
        }
        using var re = await PdfDocument.OpenAsync(path);
        var text = (await re.GetPageTextAsync(0)).Text.Trim();
        Assert.Equal(Fx.TurkishPangram, text);
        // Yazı tipi alt kümesi gömülmeli: dosya tam yazı tipinden (≈750 KB) çok küçük olmalı.
        Assert.True(new FileInfo(path).Length < 200_000, $"Dosya boyutu {new FileInfo(path).Length}");
    }

    [Fact]
    public async Task SearchFindsTurkishTextAcrossPages()
    {
        using var doc = await Fx.CreateNumberedAsync(6, "Çalışma ŞARTLARI İstanbul");
        await doc.AddTextAsync(4, "Özel ışık İSTANBUL", 72, 600, 12, Fx.Font);
        var hits = await doc.SearchAsync("istanbul", new SearchOptions());
        Assert.Equal(7, hits.Count);
        Assert.Equal(2, hits.Count(h => h.PageIndex == 4));
        Assert.All(hits, h => Assert.NotEmpty(h.Rects));
        var exact = await doc.SearchAsync("İstanbul", new SearchOptions { MatchCase = true });
        Assert.Equal(6, exact.Count);
        var partial = new List<int>();
        await doc.SearchAsync("şartları", new SearchOptions(), new Progress<(int Page, IReadOnlyList<SearchHit> Hits)>(p => { lock (partial) partial.Add(p.Page); }));
    }

    [Fact]
    public async Task HitTestAndSelectionRects()
    {
        using var doc = await Fx.CreateNumberedAsync(1, "Seçim deneme satırı");
        var t = await doc.GetPageTextAsync(0);
        int idx = t.Text.IndexOf("deneme", StringComparison.Ordinal);
        Assert.True(idx > 0);
        var box = t.Boxes[idx];
        Assert.Equal(idx, t.HitTest((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2, exact: true));
        var rects = t.GetLineRects(0, t.Length - 1);
        Assert.Equal(2, rects.Count); // iki satır
    }

    [Fact]
    public async Task SaveOverOpenFileIsAtomicAndReloads()
    {
        var dir = Fx.TempDir();
        var path = await Fx.SaveNumberedAsync(dir, "belge.pdf", 3);
        using var doc = await PdfDocument.OpenAsync(path);
        await doc.InsertBlankPagesAsync(3, 2, PaperSize.A5.ToPoints());
        Assert.True(doc.IsModified);
        await doc.SaveAsync(path);
        Assert.False(doc.IsModified);
        Assert.Equal(5, doc.PageCount);
        Assert.Empty(Directory.GetFiles(dir, "*.mesly-tmp"));
        using var re = await PdfDocument.OpenAsync(path);
        Assert.Equal(5, re.PageCount);
        Assert.Equal("A5", PaperSize.Describe(re.GetPageSize(4)));
    }

    [Fact]
    public async Task FailedSaveKeepsOriginalIntact()
    {
        var dir = Fx.TempDir();
        var path = await Fx.SaveNumberedAsync(dir, "kaynak.pdf", 2);
        var before = File.ReadAllBytes(path);
        using var doc = await PdfDocument.OpenAsync(path);
        await doc.DeletePagesAsync(new[] { 1 });
        var badTarget = Path.Combine(dir, "klasor-adi");
        Directory.CreateDirectory(badTarget); // hedef bir klasör: taşıma başarısız olmalı
        await Assert.ThrowsAsync<PdfException>(() => doc.SaveAsync(badTarget));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(1, doc.PageCount); // belge kullanılabilir durumda
        var img = await doc.RenderAsync(0, new RenderRequest(100, 141));
        Assert.Equal(100, img.Width);
    }

    [Fact]
    public async Task BookmarksAreWrittenAndRemapped()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "yerimi.pdf");
        using (var doc = await Fx.CreateNumberedAsync(5))
        {
            var root = new Bookmark { Title = "Giriş — ÇĞİÖŞÜ", PageIndex = 0 };
            root.Children.Add(new Bookmark { Title = "Alt bölüm ığüşöç", PageIndex = 2 });
            await doc.SetBookmarksAsync(new[] { root, new Bookmark { Title = "Son", PageIndex = 4 }, new Bookmark { Title = "Web", Uri = "https://mesly.example" } });
            await doc.DeletePagesAsync(new[] { 1 }); // sayfa 3 → 2, sayfa 5 → 4 olmalı
            await doc.SaveAsync(path);
        }
        using var re = await PdfDocument.OpenAsync(path);
        var bms = await re.GetBookmarksAsync();
        Assert.Equal(3, bms.Count);
        Assert.Equal("Giriş — ÇĞİÖŞÜ", bms[0].Title);
        Assert.Equal(0, bms[0].PageIndex);
        Assert.Equal("Alt bölüm ığüşöç", bms[0].Children[0].Title);
        Assert.Equal(1, bms[0].Children[0].PageIndex);
        Assert.Equal(3, bms[1].PageIndex);
        Assert.Equal("https://mesly.example", bms[2].Uri);
        Assert.Equal("Sayfa 3", await Fx.FirstLineAsync(re, 1));
    }

    [Fact]
    public async Task SnapshotsRestorePreviousState()
    {
        var dir = Fx.TempDir();
        using var doc = await Fx.CreateNumberedAsync(4);
        var snap = await doc.CreateSnapshotAsync(dir);
        await doc.DeletePagesAsync(new[] { 0, 1 });
        Assert.Equal(2, doc.PageCount);
        await doc.RestoreSnapshotAsync(snap);
        Assert.Equal(4, doc.PageCount);
        Assert.Equal(new[] { "Sayfa 1", "Sayfa 2", "Sayfa 3", "Sayfa 4" }, await Fx.PageLabelsAsync(doc));
    }
}
