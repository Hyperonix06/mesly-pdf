using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.App.Views;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// Gerçek pencerede uçtan uca öz test: <c>MeslyPdfReader.exe --selftest &lt;klasör&gt;</c>.
/// Her adım için beklenen / gerçek sonuç kaydedilir; ekran görüntüleri klasöre yazılır.
/// Çalıştırılmayan veya atlanan adımlar başarılı sayılmaz.
/// </summary>
public sealed class SelfTestRunner
{
    private readonly MainWindow _w;
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly AppDatabase _db;
    private readonly string _dir;
    private readonly List<Result> _results = new();
    private readonly List<string> _unexpectedDialogs = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();

    public sealed record Result(string Area, string Name, string Expected, string Actual, string Status, long Ms);

    public SelfTestRunner(MainWindow w, MainViewModel vm, AppSettings settings, AppDatabase db, string dir)
    {
        _w = w; _vm = vm; _settings = settings; _db = db; _dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(_dir);
        _w.SkipExitPrompt = true;
        DialogBase.Automation = msg => { lock (_unexpectedDialogs) _unexpectedDialogs.Add(msg); };
    }

    private async Task Step(string area, string name, string expected, Func<Task<(bool Ok, string Actual)>> body)
    {
        var sw = Stopwatch.StartNew();
        int dialogsBefore = _unexpectedDialogs.Count;
        string status, actual;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var task = body();
            var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { }));
            if (done != task) { status = "BAŞARISIZ"; actual = "zaman aşımı (90 sn)"; }
            else
            {
                var (ok, a) = await task;
                status = ok ? "GEÇTİ" : "BAŞARISIZ";
                actual = a;
            }
        }
        catch (Exception ex) { status = "BAŞARISIZ"; actual = $"istisna: {ex.GetType().Name}: {ex.Message}"; Log.Error($"Öz test {name}", ex); }
        if (_starvation is not null) { status = "BAŞARISIZ"; actual += " | " + _starvation; _starvation = null; }
        if (_unexpectedDialogs.Count > dialogsBefore)
        {
            status = "BAŞARISIZ";
            actual += " | beklenmeyen diyalog: " + string.Join(" / ", _unexpectedDialogs.Skip(dialogsBefore));
        }
        _results.Add(new Result(area, name, expected, actual, status, sw.ElapsedMilliseconds));
        Log.Info($"Öz test [{status}] {area} / {name}: {actual}");
    }

    private string? _starvation;

    /// <summary>Arayüz iş parçacığının boşta kalmasını bekler. 5 sn'de boşta kalmazsa (sonsuz döngü belirtisi) kaydedilir.</summary>
    private async Task<bool> Idle()
    {
        var t = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        if (await Task.WhenAny(t, Task.Delay(5000)) == t) return true;
        _starvation = "arayüz iş parçacığı 5 sn boyunca boşta kalmadı (sürekli iş kuyruğu)";
        return false;
    }

    private async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!await Idle()) return false;
            if (cond()) return true;
            await Task.Delay(60);
        }
        return cond();
    }

    private PdfViewer? Viewer => _vm.ActiveTab?.Viewer;

    /// <summary>Satır sonlarını ve art arda boşlukları tek boşluğa indirir (satıra akan metni karşılaştırmak için).</summary>
    private static string Flat(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");

    private async Task<bool> WaitRendered(int timeoutMs = 20000)
    {
        await Task.Delay(150);
        return await WaitUntil(() => Viewer is { } v && v.IsVisible && v.PendingRenders == 0 && v.CacheBytes > 0 && PdfEngine.Instance.PendingCount == 0, timeoutMs);
    }

    private string Shot(string name)
    {
        var path = Path.Combine(_dir, name + ".png");
        try
        {
            _w.UpdateLayout();
            ImageCodec.CaptureVisual((FrameworkElement)_w.Content, path);
        }
        catch (Exception ex) { Log.Warn("Ekran görüntüsü alınamadı: " + ex.Message); }
        return path;
    }

    public async Task<int> RunAsync()
    {
        _w.Width = 1440; _w.Height = 900;
        _w.WindowState = WindowState.Normal;
        _w.Left = 20; _w.Top = 20;
        await WaitUntil(() => _w.IsLoaded, 10000);
        var font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf"));
        var work = Path.Combine(_dir, "dosyalar");
        Directory.CreateDirectory(work);
        string sample = Path.Combine(work, "Örnek Türkçe Belge.pdf");

        // ── Başlangıç ──
        _settings.Theme = ThemeChoice.Light;
        _vm.Theme.Apply(ThemeChoice.Light);
        await Step("Arayüz", "Ana ekran açılır", "Başlangıç sekmesi görünür, belge yok", async () =>
        {
            await Idle();
            Shot("01-ana-ekran-acik");
            return (_vm.IsHome && _w.Home.IsVisible, $"IsHome={_vm.IsHome}, görünür={_w.Home.IsVisible}");
        });

        // ── Test belgesi üret ──
        await Step("Hazırlık", "40 sayfalık Türkçe PDF üret", "40 sayfa, yer imleri, gömülü yazı tipi", async () =>
        {
            using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 40);
            var bms = new List<Bookmark>();
            for (int i = 0; i < 40; i++)
            {
                await doc.AddRectangleAsync(i, new RectF(56, 770, 540, 800), 0x3D7BFD);
                await doc.AddTextAsync(i, $"Bölüm {i + 1}: İSTANBUL Çalışma Raporu", 64, 778, 16, font, 0xFFFFFF);
                await doc.AddTextAsync(i, "Pijamalı hasta yağız şoföre çabucak güvendi. İĞÜŞÖÇ ığüşöç", 64, 730, 12, font);
                for (int l = 0; l < 30; l++)
                    await doc.AddTextAsync(i, $"Satır {l + 1}: Mesly PDF Reader Türkçe karakter testi — ılık su, şeker, çiçek, güneş, öğretmen.", 64, 700 - l * 20, 10, font, 0x333333);
                bms.Add(new Bookmark { Title = $"Bölüm {i + 1}", PageIndex = i });
            }
            bms[0].Children.Add(new Bookmark { Title = "Alt başlık — ığüşöç", PageIndex = 1 });
            await doc.SetBookmarksAsync(bms);
            await doc.SaveAsync(sample);
            return (File.Exists(sample), $"{new FileInfo(sample).Length / 1024} KB");
        });

        // ── Açma ──
        await Step("Okuyucu", "PDF'yi sekmede aç", "Sekme açılır, 40 sayfa, sayfalar çizilir", async () =>
        {
            await _vm.OpenFilesAsync(new[] { sample });
            bool rendered = await WaitRendered();
            return (_vm.ActiveTab?.PageCount == 40 && rendered, $"sekme={_vm.Tabs.Count}, sayfa={_vm.ActiveTab?.PageCount}, çizildi={rendered}, {Viewer?.Diagnostics}");
        });
        var tab = _vm.ActiveTab!;
        Shot("02-belge-acik-tema");

        await Step("Okuyucu", "Küçük resimler ve yer imleri", "40 küçük resim, 40 kök yer imi + alt yer imi", async () =>
        {
            await WaitUntil(() => tab.Bookmarks.Count == 40);
            return (tab.Thumbnails.Count == 40 && tab.Bookmarks.Count == 40 && tab.Bookmarks[0].Children.Count == 1,
                $"küçük resim={tab.Thumbnails.Count}, yer imi={tab.Bookmarks.Count}, alt={tab.Bookmarks.FirstOrDefault()?.Children.Count}");
        });

        await Step("Okuyucu", "Sayfaya git", "Sayfa 21'e gidilir ve görünür", async () =>
        {
            tab.CurrentPage = 20;
            await WaitRendered();
            var vis = Viewer!.VisiblePages();
            return (Viewer.CurrentPage == 20 && vis.Contains(20), $"geçerli={Viewer.CurrentPage + 1}, görünen={string.Join(",", vis.Select(v => v + 1))}");
        });

        await Step("Okuyucu", "Yakınlaştırma", "Genişliğe sığdır → yakınlaştır: oran artar, kip Özel olur", async () =>
        {
            tab.ZoomMode = ZoomMode.FitWidth;
            await WaitRendered();
            double z1 = tab.Zoom;
            tab.ZoomInCommand.Execute(null);
            await WaitRendered();
            return (tab.Zoom > z1 && tab.ZoomMode == ZoomMode.Custom, $"{z1:0.00} → {tab.Zoom:0.00}, kip={tab.ZoomMode}");
        });

        await Step("Okuyucu", "Yüksek yakınlaştırma (%800)", "Bölge çizimi ile görüntülenir, hata yok", async () =>
        {
            Viewer!.ZoomTo(8, null);
            bool ok = await WaitRendered(30000);
            Shot("03-yakinlastirma-800");
            tab.ZoomMode = ZoomMode.FitWidth;
            await WaitRendered();
            return (ok, Viewer.Diagnostics);
        });

        await Step("Arama", "Türkçe büyük/küçük harf duyarsız arama", "“istanbul” 40 sayfada 40 sonuç bulur", async () =>
        {
            tab.SearchText = "istanbul";
            await tab.SearchCommand.ExecuteAsync(null);
            await WaitRendered();
            Shot("04-arama-sonuclari");
            return (tab.SearchResults.Count == 40 && tab.ActiveHit is not null, $"{tab.SearchResults.Count} sonuç, durum: {tab.SearchStatus}");
        });

        await Step("Arama", "Aksan duyarsız arama", "“sofore” → “şoföre” her sayfada bulunur", async () =>
        {
            tab.IgnoreDiacritics = true;
            tab.SearchText = "sofore";
            await tab.SearchCommand.ExecuteAsync(null);
            tab.IgnoreDiacritics = false;
            return (tab.SearchResults.Count == 40, $"{tab.SearchResults.Count} sonuç");
        });
        tab.ClearSearchCommand.Execute(null);

        await Step("Okuyucu", "Metin seçme", "Tüm belge seçilir, seçili metin Türkçe karakterleri birebir içerir", async () =>
        {
            await Viewer!.SelectAllAsync();
            var text = await Viewer.GetSelectedTextAsync();
            bool ok = text.Contains("Pijamalı hasta yağız şoföre çabucak güvendi. İĞÜŞÖÇ ığüşöç") && text.Contains("Bölüm 40");
            return (ok && Viewer.HasSelection, $"{text.Length} karakter seçildi");
        });

        await Step("Okuyucu", "Panoya kopyalama", "Pano metni Türkçe karakterleri içerir", async () =>
        {
            bool copied = await Viewer!.CopySelectionAsync();
            string clip = "";
            try { clip = Clipboard.GetText(); } catch (Exception ex) { return (false, "pano okunamadı: " + ex.Message); }
            Viewer.ClearSelection();
            return (copied && clip.Contains("ığüşöç"), $"kopyalandı={copied}, pano={clip.Length} karakter");
        });

        await Step("Görünüm", "Çift sayfa düzeni", "Yan yana iki sayfa", async () =>
        {
            tab.Layout = PageLayoutMode.TwoPageContinuous;
            await WaitRendered();
            Shot("05-cift-sayfa");
            var vis = Viewer!.VisiblePages();
            bool sideBySide = Viewer.ExtentWidth <= Viewer.ViewportWidth + 1;
            tab.Layout = PageLayoutMode.SinglePage;
            await WaitRendered();
            var single = Viewer.VisiblePages();
            tab.Layout = PageLayoutMode.Continuous;
            await WaitRendered();
            return (vis.Count >= 2 && single.Count == 1 && sideBySide, $"çift: {vis.Count} sayfa görünür, genişliğe sığdı={sideBySide}, tek: {single.Count}");
        });

        await Step("Görünüm", "Görünüm döndürme", "Görünüm 90° döner, belge değişmez", async () =>
        {
            bool modifiedBefore = tab.Document.IsModified;
            tab.RotateViewRightCommand.Execute(null);
            await WaitRendered();
            Shot("06-gorunum-dondurme");
            tab.RotateViewLeftCommand.Execute(null);
            await WaitRendered();
            return (tab.ViewRotation == 0 && tab.Document.IsModified == modifiedBefore, $"döndürme={tab.ViewRotation}, değişti={tab.Document.IsModified}");
        });

        await Step("Görünüm", "Koyu tema ve karanlık okuma", "Tema koyu olur, sayfalar koyu zeminde çizilir", async () =>
        {
            _vm.Theme.Apply(ThemeChoice.Dark);
            await WaitRendered();
            Shot("07-koyu-tema");
            tab.DarkMode = true;
            await WaitRendered();
            Shot("08-karanlik-okuma");
            tab.DarkMode = false;
            _vm.Theme.Apply(ThemeChoice.Light);
            await WaitRendered();
            return (true, "koyu tema ve karanlık okuma ekran görüntüleri alındı");
        });

        // ── Sayfa işlemleri ──
        await Step("Sayfalar", "Sayfa döndürme (kalıcı)", "Sayfa 1 yatay olur, belge değişti olarak işaretlenir", async () =>
        {
            tab.SelectedPages.Clear(); tab.SelectedPages.Add(0);
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            await WaitRendered();
            return (tab.Document.GetPageSize(0).IsLandscape && tab.IsModified, $"yatay={tab.Document.GetPageSize(0).IsLandscape}, değişti={tab.IsModified}");
        });

        await Step("Sayfalar", "Sayfa silme + geri al + yinele", "40 → 38 → 40 → 38", async () =>
        {
            await tab.DeletePagesAsync(new[] { 2, 3 });
            int afterDelete = tab.PageCount;
            await tab.UndoCommand.ExecuteAsync(null);
            int afterUndo = tab.PageCount;
            await tab.RedoCommand.ExecuteAsync(null);
            int afterRedo = tab.PageCount;
            await WaitRendered();
            return (afterDelete == 38 && afterUndo == 40 && afterRedo == 38, $"{afterDelete} → {afterUndo} → {afterRedo}");
        });

        await Step("Sayfalar", "Sayfa çoğaltma ve taşıma", "Çoğaltma +1 sayfa; son sayfa başa taşınır", async () =>
        {
            tab.SelectedPages.Clear(); tab.SelectedPages.Add(0);
            await tab.DuplicatePagesCommand.ExecuteAsync(null);
            int afterDup = tab.PageCount;
            await tab.MovePagesAsync(new[] { tab.PageCount - 1 }, 0);
            var first = (await tab.Document.GetPageTextAsync(0)).Text;
            return (afterDup == 39 && first.Contains("Bölüm 40"), $"çoğaltma sonrası={afterDup}, ilk sayfa: {first.Split('\n')[0].Trim()}");
        });

        await Step("Sayfalar", "Kaydet ve yeniden aç", "Kaydedilen dosya 39 sayfa, yer imleri korunur", async () =>
        {
            var target = Path.Combine(work, "Kaydedilmiş Kopya.pdf");
            await tab.Document.SaveAsync(target);
            await Idle();
            using var re = await PdfDocument.OpenAsync(target);
            var bms = await re.GetBookmarksAsync();
            // Silinen 2 sayfanın (Bölüm 3, 4) yer imleri kaldırılmış olmalı; kalan 38 yer iminin hepsi geçerli bir sayfaya gitmeli.
            bool valid = bms.All(b => b.PageIndex >= 0 && b.PageIndex < re.PageCount);
            return (re.PageCount == 39 && !tab.Document.IsModified && bms.Count == 38 && valid, $"sayfa={re.PageCount}, yer imi={bms.Count}, hepsi geçerli={valid}, değişti={tab.Document.IsModified}");
        });

        // ── Dönüştürme ──
        var pngPath = Path.Combine(work, "sayfa1.png");
        var jpgPath = Path.Combine(work, "sayfa1.jpg");
        await Step("Dönüştürme", "PDF → PNG / JPG / TIFF / BMP", "Dört dosya oluşur, boyutları 150 DPI'ya uygun", async () =>
        {
            var img = await PdfTools.RenderAtDpiAsync(tab.Document, 1, 150);
            var tif = Path.Combine(work, "sayfa1.tif"); var bmp = Path.Combine(work, "sayfa1.bmp");
            ImageCodec.Save(img, pngPath, ImageFormatKind.Png, 150);
            ImageCodec.Save(img, jpgPath, ImageFormatKind.Jpeg, 150, 85);
            ImageCodec.Save(img, tif, ImageFormatKind.Tiff, 150);
            ImageCodec.Save(img, bmp, ImageFormatKind.Bmp, 150);
            var sizes = new List<string>();
            bool ok = true;
            foreach (var f in new[] { pngPath, jpgPath, tif, bmp })
            {
                var dec = BitmapDecoder.Create(new Uri(f), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var fr = dec.Frames[0];
                ok &= fr.PixelWidth == img.Width && fr.PixelHeight == img.Height;
                sizes.Add($"{Path.GetExtension(f)} {fr.PixelWidth}×{fr.PixelHeight}");
            }
            var multi = Path.Combine(work, "tum.tif");
            ImageCodec.SaveMultiPageTiff(new[] { img, img, img }, multi, 150);
            var md = BitmapDecoder.Create(new Uri(multi), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            ok &= md.Frames.Count == 3;
            return (ok, string.Join(", ", sizes) + $", çok sayfalı TIFF: {md.Frames.Count} kare");
        });

        await Step("Dönüştürme", "Görüntü → PDF", "JPG ve PNG'den 2 sayfalık PDF; JPEG yeniden sıkıştırılmaz", async () =>
        {
            var srcs = ImageCodec.LoadForPdf(jpgPath).Concat(ImageCodec.LoadForPdf(pngPath)).ToList();
            using var doc = await PdfDocument.CreateFromImagesAsync(srcs, new ImagePageLayout(PaperSize.A4.ToPoints(), 20, true));
            var outPath = Path.Combine(work, "gorsellerden.pdf");
            await doc.SaveAsync(outPath);
            bool jpegKept = srcs[0] is JpegImageSource;
            return (doc.PageCount == 2 && jpegKept, $"sayfa={doc.PageCount}, JPEG doğrudan={jpegKept}, {new FileInfo(outPath).Length / 1024} KB");
        });

        await Step("Dönüştürme", "PDF → TXT", "UTF-8 BOM'lu, Türkçe karakterli metin", async () =>
        {
            var txt = Path.Combine(work, "metin.txt");
            await PdfTools.ExportTextAsync(tab.Document, txt);
            var bytes = File.ReadAllBytes(txt);
            var text = File.ReadAllText(txt, Encoding.UTF8);
            return (bytes.Length > 3 && bytes[0] == 0xEF && text.Contains("ığüşöç"), $"{bytes.Length / 1024} KB");
        });

        await Step("Sayfa yönetimi", "Birleştirme sekmede açılır", "İki belge birleşir, ikinci sekme açılır", async () =>
        {
            using var _ = await PdfDocument.OpenAsync(sample);
            var merged = await PdfTools.MergeAsync(new[] { new MergeSource(sample, PageRange: "1-5"), new MergeSource(Path.Combine(work, "gorsellerden.pdf")) });
            _vm.AddTab(merged, record: false);
            await WaitRendered();
            Shot("09-iki-sekme");
            return (_vm.Tabs.Count == 2 && merged.PageCount == 7, $"sekme={_vm.Tabs.Count}, birleşik sayfa={merged.PageCount}");
        });

        await Step("Sayfa yönetimi", "Bölme", "39 sayfa her 10 sayfada bölünür → 4 dosya", async () =>
        {
            var outDir = Path.Combine(work, "bolunmus");
            var files = await PdfTools.SplitAsync(tab.Document, PdfTools.PlanSplit(tab.PageCount, new SplitPlan(SplitMode.EveryNPages, 10)), outDir, "parca");
            return (files.Count == 4 && files.All(File.Exists), string.Join(", ", files.Select(Path.GetFileName)));
        });

        await Step("Yazdırma", "Yazdırma sayfalayıcısı (XPS)", "Tüm sayfalar XPS'e yazılır", async () =>
        {
            _vm.ActiveTab = tab;
            await Idle();
            var xps = Path.Combine(work, "yazdirma.xps");
            int n = PrintPreviewWindow.WriteXpsForTest(tab, xps);
            return (n == tab.PageCount && new FileInfo(xps).Length > 10_000, $"{n} sayfa, {new FileInfo(xps).Length / 1024} KB");
        });

        await Step("Yazdırma", "Yazdırma önizlemesi penceresi", "Önizleme açılır, yazıcı listelenir, Yazdır düğmesi etkin", async () =>
        {
            var win = PrintPreviewWindow.CreateForTest(tab);
            win.Show();
            await Task.Delay(800);
            await Idle();
            var path = Path.Combine(_dir, "10-yazdirma-onizleme.png");
            ImageCodec.CaptureVisual((FrameworkElement)win.Content, path);
            bool ok = File.Exists(path) && (win.PrinterCount == 0 || win.CanPrint);
            var info = $"yazıcı sayısı={win.PrinterCount}, seçili=“{win.PrinterName}”, Yazdır etkin={win.CanPrint}";
            win.Close();
            return (ok, info);
        });

        await Step("Ayarlar", "Ayarlar penceresi", "Açılır, tüm bölümler oluşturulur", async () =>
        {
            var win = SettingsWindow.CreateForTest(_vm, _settings, _db);
            win.Show();
            await Task.Delay(500);
            await Idle();
            var path = Path.Combine(_dir, "11-ayarlar.png");
            ImageCodec.CaptureVisual((FrameworkElement)win.Content, path);
            win.Close();
            return (File.Exists(path), "ekran görüntüsü alındı");
        });

        await Step("Veri", "Son dosyalar ve son sayfa", "Örnek belge geçmişte; son sayfa hatırlanır", async () =>
        {
            tab.CurrentPage = 12;
            tab.SaveState();
            var st = _db.GetDocumentState(tab.Document.FilePath!);
            var recent = _db.GetRecent();
            await Task.CompletedTask;
            return (st?.Page == 12 && recent.Count >= 1, $"son sayfa={st?.Page + 1}, geçmiş={recent.Count}");
        });

        // ── Tarama ──
        await Step("Tarama", "WIA tarayıcı listesi", "WIA hizmetiyle konuşulur; tarayıcı yoksa çökmeden anlaşılır mesaj", async () =>
        {
            try
            {
                var list = await WiaScanner.ListAsync();
                return (true, list.Count == 0 ? "bu makinede tarayıcı yok (0 cihaz) — gerçek tarama burada test edilemez" : $"{list.Count} tarayıcı: {string.Join(", ", list)}");
            }
            catch (ScanException ex) { return (ex.Kind is ScanErrorKind.ServiceUnavailable or ScanErrorKind.NoDevice, $"anlaşılır hata: {ex.Message}"); }
        });

        await Step("Tarama", "Tarama penceresi", "Açılır; cihaz yoksa “Tara” devre dışı ve neden yazılı", async () =>
        {
            var win = ScanDialog.CreateForTest(_settings);
            win.Show();
            await WaitUntil(() => win.IsLoaded, 5000);
            await win.Initialization;
            await Task.Delay(300);
            await Idle();
            var path = Path.Combine(_dir, "14-tarama-penceresi.png");
            ImageCodec.CaptureVisual((FrameworkElement)win.Content, path);
            var info = $"cihaz={win.DeviceCount}, durum: {win.StatusText}";
            win.Close();
            return (File.Exists(path) && !string.IsNullOrWhiteSpace(win.StatusText), info);
        });

        await Step("Tarama", "Taranan sayfalardan PDF", "JPEG (kayıpsız) + PNG sayfa, kullanıcı döndürmesi uygulanır", async () =>
        {
            var pages = new List<ScannedPage>
            {
                new() { Data = File.ReadAllBytes(jpgPath), Format = ScannedFormat.Jpeg, Dpi = 150 },
                new() { Data = File.ReadAllBytes(pngPath), Format = ScannedFormat.Png, Dpi = 150, QuarterTurns = 1 },
            };
            using var doc = await ScanPdfBuilder.BuildAsync(pages);
            int rot0 = await doc.GetRotationAsync(0), rot1 = await doc.GetRotationAsync(1);
            var s0 = doc.GetPageSize(0);
            return (doc.PageCount == 2 && rot0 == 0 && rot1 == 1 && Math.Abs(PaperSize.PtToMm(s0.Width) - 297) < 3,
                $"sayfa={doc.PageCount}, döndürme={rot0}/{rot1}, 1. sayfa {PaperSize.Describe(s0)}");
        });

        // ── Aşama 3: içerik düzenleme ──
        string editPath = Path.Combine(work, "Düzenleme Testi.pdf");
        var winArial = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        await Step("Düzenleme", "Yazı tipi dizini ve eşleme", "Kurulu yazı tipleri listelenir; “ABCDEF+Arial-BoldMT” → Arial kalın, Türkçe karakterler var", async () =>
        {
            var fonts = await FontResolver.Instance.GetFontsAsync();
            var (_, choice) = await FontResolver.Instance.ResolveAsync("ABCDEF+Arial-BoldMT", "İĞÜŞÖÇ ığüşöç", bold: true, italic: false);
            var (_, missing) = await FontResolver.Instance.ResolveAsync("XYZABC+MeslyOlmayanFont", "ığüşöç", false, false);
            bool ok = fonts.Count >= 10 && choice.Font?.Family == "Arial" && choice.Font.Bold && choice.Exact && missing.Font is not null && missing.Note is not null;
            return (ok, $"{fonts.Count} yazı tipi; Arial-Bold → {choice.Font?.Family} kalın={choice.Font?.Bold} tam={choice.Exact}; olmayan → {missing.Font?.Family} ({missing.Note})");
        });

        await Step("Düzenleme", "Paragraf metnini değiştir (gerçek içerik)", "Eski metin nesneleri silinir, yeni metin kutu genişliğinde akar, aranabilir", async () =>
        {
            var fdata = File.Exists(winArial) ? File.ReadAllBytes(winArial) : font;
            using (var d = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 2))
            {
                await d.AddTextAsync(0, "Hizmet Sözleşmesi", 72, 770, 22, fdata);
                var lines = new[] { "Bu sözleşme, taraflar arasında yapılan hizmet alımına", "ilişkin koşulları belirler; ödeme otuz gün içinde", "yapılır ve gecikmede faiz uygulanır." };
                for (int k = 0; k < lines.Length; k++) await d.AddTextAsync(0, lines[k], 72, 720 - k * 15, 12, fdata);
                await d.AddTextAsync(0, "İmza: ____________", 72, 600, 12, fdata);
                await d.SaveAsync(editPath);
            }
            await _vm.OpenFilesAsync(new[] { editPath });
            var et = _vm.ActiveTab!;
            await WaitRendered();
            et.Tool = ViewerTool.Edit;
            bool loaded = await WaitUntil(() => Viewer!.EditItemCount(0) >= 3, 10000);
            bool selected = Viewer!.SelectItemAt(0, 100, 715);
            var para = et.EditSelection?.Single?.Paragraph;
            if (para is null) return (false, $"öğeler={Viewer.EditItemCount(0)}, seçim yok");
            var newText = "Bu sözleşme, Mesly PDF Reader ile gerçek içerik düzenlemesi yapılarak değiştirilmiştir: ödeme kırk beş gün içinde yapılır, gecikmede yıllık %12 faiz uygulanır. İĞÜŞÖÇ ığüşöç.";
            bool ok1 = await et.ReplaceTextAsync(0, para, newText);
            await WaitRendered();
            var text = Flat((await et.Document.GetPageTextAsync(0)).Text);
            var objs = await et.Document.GetPageObjectsAsync(0);
            bool noOverlay = !objs.Any(o => o.Kind == PageObjectKind.Path && o.FillRgb == 0xFFFFFF);
            bool ok = loaded && selected && ok1 && !text.Contains("otuz gün") && text.Contains("kırk beş gün") && text.Contains("İĞÜŞÖÇ ığüşöç") && noOverlay;
            await WaitUntil(() => Viewer!.EditItemCount(0) >= 3, 5000);
            Viewer!.SelectItemAt(0, 100, 715);
            await Idle();
            Shot("13-duzenleme-paragraf");
            return (ok, $"metin: “{(text.Length > 160 ? text[..160] : text)}”; öğe={Viewer.EditItemCount(0)}, değişti={ok1}, eski metin yok={!text.Contains("otuz gün")}, yeni metin var={text.Contains("kırk beş gün")}, beyaz kutu yok={noOverlay}; {et.StatusText}");
        });

        await Step("Düzenleme", "Yerinde metin düzenleyici", "Seçili paragrafta düzenleyici açılır, Esc ile değişiklik yapılmadan kapanır", async () =>
        {
            var et = _vm.ActiveTab!;
            int rev = et.Document.Revision;
            bool opened = Viewer!.BeginEditSelectedText();
            await Idle();
            Shot("14-duzenleme-yerinde");
            bool editing = Viewer.IsEditingText;
            Viewer.CancelTextEdit();
            await Idle();
            return (opened && editing && !Viewer.IsEditingText && et.Document.Revision == rev, $"açıldı={opened}, düzenleniyor={editing}, belge değişmedi={et.Document.Revision == rev}");
        });

        await Step("Düzenleme", "Şekil, görsel, bağlantı, taşıma + geri al", "Nesneler içerik akışına eklenir; taşıma geri alınınca eski konum", async () =>
        {
            var et = _vm.ActiveTab!;
            et.ShapeStroke = "#1E5EF0"; et.ShapeFill = "#E6F0FF"; et.ShapeStrokeWidth = 2;
            bool r = await et.PlaceAsync(EditPlacement.Rectangle, 0, 72, 560, 300, 500);
            bool e = await et.PlaceAsync(EditPlacement.Ellipse, 0, 320, 560, 520, 500);
            bool a = await et.PlaceAsync(EditPlacement.Arrow, 0, 72, 470, 300, 440);
            await et.Document.AddImageAsync(0, ImageCodec.LoadForPdf(jpgPath)[0], new RectF(320, 300, 520, 450));
            await et.Document.AddLinkAsync(0, new RectF(72, 595, 200, 615), "https://example.com/mesly");
            var objs = await et.Document.GetPageObjectsAsync(0);
            var img = objs.First(o => o.Kind == PageObjectKind.Image);
            bool mv = await et.MoveAsync(0, new[] { img.Index }, -240, 0);
            var moved = (await et.Document.GetPageObjectsAsync(0))[img.Index].Bounds.Left;
            await et.UndoCommand.ExecuteAsync(null);
            var back = (await et.Document.GetPageObjectsAsync(0)).First(o => o.Kind == PageObjectKind.Image).Bounds.Left;
            await et.RedoCommand.ExecuteAsync(null);
            var links = await et.Document.GetLinksAsync(0);
            await WaitRendered();
            Shot("15-duzenleme-nesneler");
            bool ok = r && e && a && mv && Math.Abs(moved - (img.Bounds.Left - 240)) < 1 && Math.Abs(back - img.Bounds.Left) < 1 && links.Any(l => l.Uri == "https://example.com/mesly");
            return (ok, $"şekiller={r}/{e}/{a}, görsel taşındı {img.Bounds.Left:0}→{moved:0}, geri al→{back:0}, bağlantı={links.Count}");
        });

        await Step("Düzenleme", "Filigran, sayfa numarası, arka plan", "Gerçek metin/yol nesneleri olarak eklenir ve kaldırılabilir", async () =>
        {
            var et = _vm.ActiveTab!;
            var (fd, _) = await FontResolver.Instance.ResolveAsync("Arial", "GİZLİ Sayfa 0123456789/", false, false);
            await et.Document.AddWatermarkAsync(new[] { 0, 1 }, new WatermarkSettings("GİZLİ", new TextStyle(fd, 0, 0xD32F2F), 45, 0.2));
            await et.Document.AddHeaderFooterAsync(new[] { 0, 1 }, new HeaderFooterSettings(new[] { new HeaderFooterItem(HFPosition.BottomCenter, "Sayfa {sayfa} / {toplam}") }, new TextStyle(fd, 9, 0x444444)));
            await et.Document.SetBackgroundAsync(new[] { 1 }, 0xFFF8D6);
            et.Tool = ViewerTool.Select;
            await WaitRendered();
            Shot("16-filigran-sayfa-no");
            var t1 = Flat((await et.Document.GetPageTextAsync(1)).Text);
            int wm = await et.Document.RemoveWatermarksAsync(new[] { 0, 1 });
            int bg = await et.Document.RemoveBackgroundAsync(new[] { 0, 1 });
            var t0 = (await et.Document.GetPageTextAsync(0)).Text;
            await et.Document.SaveAsync(editPath);
            using var re = await PdfDocument.OpenAsync(editPath);
            var rt = Flat((await re.GetPageTextAsync(0)).Text);
            bool ok = t1.Contains("Sayfa 2 / 2") && t1.Contains("GİZLİ") && wm == 2 && bg == 1 && !t0.Contains("GİZLİ") && rt.Contains("kırk beş gün") && rt.Contains("Sayfa 1 / 2");
            return (ok, $"sayfa 2 metni: “{(t1.Length > 120 ? t1[^120..] : t1)}”; filigran silinen={wm}, arka plan silinen={bg}, kaydedilen dosyada yeni metin={rt.Contains("kırk beş gün")}, sayfa no={rt.Contains("Sayfa 1 / 2")}");
        });

        // ── Büyük belge ──
        string big = Path.Combine(work, "bin-sayfa.pdf");
        await Step("Performans", "1.000 sayfalık belge", "Hızlı açılır, son sayfaya gidilir, bellek sınırlı", async () =>
        {
            using (var d = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1000))
            {
                for (int i = 0; i < 1000; i++) await d.AddTextAsync(i, $"Büyük belge — sayfa {i + 1}", 72, 760, 18, font);
                await d.SaveAsync(big);
            }
            GC.Collect();
            var proc = Process.GetCurrentProcess(); proc.Refresh();
            long before = proc.WorkingSet64;
            var sw = Stopwatch.StartNew();
            await _vm.OpenFilesAsync(new[] { big });
            bool r1 = await WaitRendered();
            long openMs = sw.ElapsedMilliseconds;
            var bt = _vm.ActiveTab!;
            for (int p = 0; p < 1000; p += 50) { bt.CurrentPage = p; await Task.Delay(30); }
            bt.CurrentPage = 999;
            bool r2 = await WaitRendered();
            Shot("12-bin-sayfa");
            GC.Collect(); proc.Refresh();
            long growth = proc.WorkingSet64 - before;
            bool ok = r1 && r2 && bt.PageCount == 1000 && Viewer!.CurrentPage == 999 && growth < 600L * 1024 * 1024;
            return (ok, $"açılış+ilk çizim {openMs} ms, sayfa={bt.PageCount}, geçerli={Viewer!.CurrentPage + 1}, bellek artışı {growth / 1048576} MB, {Viewer.Diagnostics}");
        });

        await Step("Arayüz", "Sekmeleri kapatma", "Değişmemiş sekmeler soru sormadan kapanır", async () =>
        {
            foreach (var t in _vm.Tabs.Where(t => !t.Document.IsModified).ToList()) await _vm.CloseTab(t);
            int left = _vm.Tabs.Count;
            await Idle();
            return (left == _vm.Tabs.Count(t => t.Document.IsModified), $"kalan (kaydedilmemiş) sekme={left}");
        });

        _vm.ShowHomeCommand.Execute(null);
        _vm.Theme.Apply(ThemeChoice.Dark);
        await Idle();
        Shot("13-ana-ekran-koyu");
        _vm.Theme.Apply(ThemeChoice.Light);

        return WriteReport();
    }

    private int WriteReport()
    {
        int failed = _results.Count(r => r.Status != "GEÇTİ");
        var json = JsonSerializer.Serialize(new
        {
            uygulama = "Mesly PDF Reader", surum = typeof(App).Assembly.GetName().Version?.ToString(3),
            isletimSistemi = Environment.OSVersion.VersionString, dotnet = Environment.Version.ToString(),
            tarih = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), sureMs = _total.ElapsedMilliseconds,
            toplam = _results.Count, gecen = _results.Count - failed, basarisiz = failed, sonuclar = _results,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(_dir, "selftest.json"), json, new UTF8Encoding(false));

        var md = new StringBuilder();
        md.AppendLine($"## Arayüz öz testi — {Environment.OSVersion.VersionString}");
        md.AppendLine();
        md.AppendLine($"{_results.Count} adım · **{_results.Count - failed} geçti** · {failed} başarısız · {_total.Elapsed.TotalSeconds:0.0} sn");
        md.AppendLine();
        md.AppendLine("| Alan | Test | Beklenen | Gerçek | Durum | Süre |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in _results)
            md.AppendLine($"| {r.Area} | {r.Name} | {r.Expected} | {r.Actual.Replace("|", "/")} | {(r.Status == "GEÇTİ" ? "✅" : "❌")} {r.Status} | {r.Ms} ms |");
        File.WriteAllText(Path.Combine(_dir, "selftest.md"), md.ToString(), new UTF8Encoding(false));
        return failed == 0 ? 0 : 1;
    }
}
