using System.Diagnostics;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace MeslyPdfReader.Tests;

/// <summary>1.000 sayfalık belge: açılış süresi, rastgele sayfa çizimi, arama ve bellek kullanımı.</summary>
public class LargeDocumentTests
{
    private readonly ITestOutputHelper _out;
    public LargeDocumentTests(ITestOutputHelper output) => _out = output;

    private static readonly Lazy<string> ThousandPagePdf = new(() =>
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "bin-sayfa.pdf");
        using var doc = Fx.CreateNumberedAsync(1000, "Uzun belge satırı: ığüşöç İĞÜŞÖÇ").GetAwaiter().GetResult();
        doc.AddTextAsync(776, "Aranan özel kelime: Kahramanmaraş", 72, 500, 12, Fx.Font).GetAwaiter().GetResult();
        doc.SaveAsync(path).GetAwaiter().GetResult();
        return path;
    });

    [Fact]
    public async Task OpensQuicklyAndRendersRandomPages()
    {
        var path = ThousandPagePdf.Value;
        var sw = Stopwatch.StartNew();
        using var doc = await PdfDocument.OpenAsync(path);
        var openMs = sw.ElapsedMilliseconds;
        Assert.Equal(1000, doc.PageCount);
        _out.WriteLine($"1000 sayfa açılış: {openMs} ms, dosya {new FileInfo(path).Length / 1024} KB");
        Assert.True(openMs < 5000, $"Açılış çok yavaş: {openMs} ms");

        var rnd = new Random(42);
        sw.Restart();
        for (int k = 0; k < 40; k++)
        {
            int p = rnd.Next(1000);
            var img = await doc.RenderAsync(p, new RenderRequest(300, 424));
            Assert.Equal(300, img.Width);
        }
        _out.WriteLine($"40 rastgele sayfa çizimi: {sw.ElapsedMilliseconds} ms");
        Assert.Equal($"Sayfa 1000", await Fx.FirstLineAsync(doc, 999));
    }

    [Fact]
    public async Task SearchesWholeDocument()
    {
        using var doc = await PdfDocument.OpenAsync(ThousandPagePdf.Value);
        var sw = Stopwatch.StartNew();
        var hits = await doc.SearchAsync("KAHRAMANMARAŞ", new SearchOptions());
        _out.WriteLine($"1000 sayfada arama: {sw.ElapsedMilliseconds} ms");
        var hit = Assert.Single(hits);
        Assert.Equal(776, hit.PageIndex);
        Assert.True(sw.ElapsedMilliseconds < 30000);
    }

    [Fact]
    public async Task MemoryStaysBoundedWhileRenderingManyPages()
    {
        using var doc = await PdfDocument.OpenAsync(ThousandPagePdf.Value);
        for (int i = 0; i < 20; i++) await doc.RenderAsync(i, new RenderRequest(600, 848));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long baseline = proc.WorkingSet64;
        for (int i = 0; i < 300; i++)
        {
            var img = await doc.RenderAsync(i % 1000 * 3 % 1000, new RenderRequest(600, 848));
            Assert.Equal(600, img.Width);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        proc.Refresh();
        long growth = proc.WorkingSet64 - baseline;
        _out.WriteLine($"300 sayfa çizimi sonrası çalışma kümesi artışı: {growth / 1048576} MB");
        Assert.True(growth < 250L * 1024 * 1024, $"Bellek beklenenden fazla arttı: {growth / 1048576} MB");
    }

    [Fact]
    public async Task PageOperationsOnLargeDocument()
    {
        var dir = Fx.TempDir();
        using var doc = await PdfDocument.OpenAsync(ThousandPagePdf.Value);
        var sw = Stopwatch.StartNew();
        await doc.DeletePagesAsync(Enumerable.Range(0, 500).Select(i => i * 2));
        await doc.MovePagesAsync(new[] { 499 }, 0);
        var target = Path.Combine(dir, "yarim.pdf");
        await doc.SaveAsync(target);
        _out.WriteLine($"500 sayfa silme + taşıma + kaydetme: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(500, doc.PageCount);
        Assert.Equal("Sayfa 1000", await Fx.FirstLineAsync(doc, 0));
        Assert.Equal("Sayfa 2", await Fx.FirstLineAsync(doc, 1));
    }
}
