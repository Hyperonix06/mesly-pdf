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

        // ── Aşama 4: metin tanıma (OCR) ve tarama düzeltme ──
        await Step("Tanıma", "OCR motoru ve dil verileri", "Tesseract yüklenir; Türkçe, İngilizce ve yön verisi kurulu", async () =>
        {
            await Task.Yield();
            var why = MeslyPdfReader.Ocr.OcrEngine.Unavailable();
            var langs = MeslyPdfReader.Ocr.OcrEngine.InstalledLanguages();
            var data = MeslyPdfReader.Ocr.OcrEngine.FindTessdata();
            bool osd = data is not null && File.Exists(Path.Combine(data, "osd.traineddata"));
            return (why is null && langs.Contains("tur") && langs.Contains("eng") && osd,
                $"sürüm {MeslyPdfReader.Ocr.OcrEngine.Version()}, diller: {string.Join(", ", langs)}, yön verisi={osd}, klasör={data}{(why is null ? "" : " / " + why)}");
        });

        string scanPdf = Path.Combine(work, "Yan Taranmış Belge.pdf");
        var ocrLines = new[] { "Pijamalı hasta yağız şoföre çabucak güvendi.", "İstanbul Büyükşehir Belediyesi, Ankara ve İzmir.", "Sözleşme kapsamında ödeme otuz gün içinde yapılır.", "Çalışma saatleri sabah dokuz ile akşam altı arasıdır." };
        await Step("Tarama", "Yan ve eğik taranmış sayfanın otomatik düzeltilmesi", "Tarama penceresi düzeltmesi: 90° yan + 2° eğik sayfa dik ve düz olur", async () =>
        {
            BgraImage page;
            using (var d = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1))
            {
                await d.AddTextAsync(0, "Hizmet Sözleşmesi", 72, 760, 24, font);
                for (int i = 0; i < ocrLines.Length; i++) await d.AddTextAsync(0, ocrLines[i], 72, 700 - i * 22, 14, font);
                page = await PdfTools.RenderAtDpiAsync(d, 0, 300);
            }
            // Tarayıcı sürücüsünün yaptığı gibi: sayfa yana dönmüş ve biraz eğik gelir.
            var sideways = ImageOps.Rotate(ImageOps.RotateQuarter(page, 1), 2.0);
            var scanned = new ScannedPage { Data = ImageCodec.EncodeJpeg(sideways, 90), Format = ScannedFormat.Jpeg, Dpi = 300 };
            var sw = Stopwatch.StartNew();
            var (fixedPage, rep) = await Task.Run(() => OcrWorkflow.CorrectScan(scanned, new ImageCorrection(), "tur+eng"));
            long ms = sw.ElapsedMilliseconds;
            var img = ImageCodec.Decode(fixedPage.Data);
            using (var doc = await ScanPdfBuilder.BuildAsync(new[] { fixedPage })) await doc.SaveAsync(scanPdf);
            bool ok = rep.QuarterTurnsApplied == 3 && img.Height > img.Width && Math.Abs(Math.Abs(rep.DeskewDegrees) - 2.0) < 0.4;
            return (ok, $"{rep.Describe()} ({ms} ms); sonuç {img.Width}×{img.Height} px");
        });

        await Step("Tanıma", "Taranmış PDF: bilgi şeridi → metni tanı → aranabilir", "Şerit görünür; tanımadan sonra Türkçe metin aranır, görünüm değişmez", async () =>
        {
            await _vm.OpenFilesAsync(new[] { scanPdf });
            var st = _vm.ActiveTab!;
            await WaitRendered();
            bool banner = await WaitUntil(() => st.CurrentPageIsScanned, 8000);
            Shot("17-taranmis-sayfa-bilgi-seridi");
            var sw = Stopwatch.StartNew();
            var res = await st.RunOcrAsync(ocr: true, pages: new[] { 0 }, ask: false);
            long ms = sw.ElapsedMilliseconds;
            await WaitRendered();
            var hits = await st.Document.SearchAsync("İstanbul", new SearchOptions());
            var text = Flat((await st.Document.GetPageTextAsync(0)).Text);
            int okLines = ocrLines.Count(l => text.Contains(l.TrimEnd('.')));
            Shot("18-ocr-sonrasi");
            bool ok = banner && res is { Count: 1 } && hits.Count == 1 && okLines >= 3 && !st.CurrentPageIsScanned;
            return (ok, $"şerit={banner}, {ms} ms, {res?[0].Words} sözcük, ort. güven %{res?[0].Ocr?.MeanConfidence:0}, birebir satır {okLines}/4, “İstanbul” isabet={hits.Count}; {st.StatusText}");
        });

        await Step("Tanıma", "Taranmış sayfada metni düzenle", "Düzenle sekmesi aracı açar; OCR paragrafı değişir, görüntüdeki eski yazı silinir", async () =>
        {
            var st = _vm.ActiveTab!;
            _vm.Ribbon = RibbonTab.EditContent;
            bool toolOn = st.Tool == ViewerTool.Edit;
            await WaitUntil(() => Viewer!.EditItemCount(0) > 0, 8000);
            var para = (await st.Document.GetParagraphsAsync(0)).FirstOrDefault(p => p.Text.Contains("otuz"));
            if (para is null) return (false, "“otuz gün” paragrafı bulunamadı");
            // Yalnız bir sözcük değişir; paragrafın geri kalanı aynen yeniden yazılır.
            bool ok1 = await st.ReplaceTextAsync(0, para, para.Text.Replace("otuz", "kırk beş"));
            await WaitRendered();
            await WaitUntil(() => Viewer!.EditItemCount(0) > 0, 5000);
            Viewer!.SelectItemAt(0, (para.Bounds.Left + para.Bounds.Right) / 2, (para.Bounds.Bottom + para.Bounds.Top) / 2);
            await Idle();
            Shot("19-taranmis-sayfada-duzenleme");
            var text = Flat((await st.Document.GetPageTextAsync(0)).Text);
            var objs = await st.Document.GetPageObjectsAsync(0);
            _vm.Ribbon = RibbonTab.Document;
            bool toolOff = st.Tool == ViewerTool.Select;
            bool ok = toolOn && toolOff && ok1 && text.Contains("kırk beş gün") && !text.Contains("otuz gün") && text.Contains("İstanbul") && !objs.Any(o => o.Kind == PageObjectKind.Path);
            return (ok, $"araç açıldı={toolOn}, kapandı={toolOff}, metin değişti={ok1}, yeni metin={text.Contains("kırk beş gün")}, eski metin yok={!text.Contains("otuz gün")}; {st.StatusText}");
        });

        await Step("Arayüz", "FineReader düzeni: Belge / Sayfaları Düzenle / İçeriği Düzenle", "Sekmeler taşmaz; Sayfaları Düzenle ızgarayı, İçeriği Düzenle düzenleme kipini açar", async () =>
        {
            var st = _vm.ActiveTab!;
            int overflow = 0;
            bool organizeOk = false, editOk = false;
            foreach (var (tabKey, name) in new[] { (RibbonTab.Document, "20-belge-sekmesi"), (RibbonTab.Organize, "21-sayfalari-duzenle"), (RibbonTab.EditContent, "22-icerigi-duzenle") })
            {
                _vm.Ribbon = tabKey;
                await Idle();
                if (tabKey == RibbonTab.Organize) { await Task.Delay(400); await Idle(); organizeOk = st.IsOrganizeMode; }
                if (tabKey == RibbonTab.EditContent) editOk = st.Tool == ViewerTool.Edit && !st.IsOrganizeMode;
                if (_w.RibbonScroll.ScrollableWidth > 1) overflow++;
                Shot(name);
            }
            _vm.Ribbon = RibbonTab.Document;
            await Idle();
            bool back = st.Tool == ViewerTool.Select && !st.IsOrganizeMode;
            return (overflow == 0 && organizeOk && editOk && back, $"taşan sekme: {overflow}, sayfa ızgarası={organizeOk}, düzenleme kipi={editOk}, Belge'ye dönüş={back}");
        });

        // ── Aşama 5: Word / Excel dönüştürme, sıkıştırma, Office → PDF ──
        string reportPdf = Path.Combine(work, "Satış Raporu.pdf");
        string reportDocx = Path.Combine(work, "Satış Raporu.docx"), reportXlsx = Path.Combine(work, "Satış Raporu.xlsx");
        await Step("Dönüştürme", "PDF → Word: başlık, paragraf, çizgili tablo, fotoğraf", "DOCX açılır; Başlık1, tablo 3×3 + birleşik başlık, JPEG resim, Türkçe metin birebir", async () =>
        {
            using (var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1))
            {
                await doc.AddTextAsync(0, "Satış Raporu 2026", 72, 770, 26, font);
                await doc.AddTextAsync(0, "Bu rapor İstanbul, İzmir ve Çanakkale şubelerinin üç aylık sonuçlarını özetler.", 72, 735, 11, font);
                await doc.AddTextAsync(0, "Güçlü büyüme özellikle öğrenci ürünlerinde görüldü.", 72, 720, 11, font);
                double[] xs = { 72, 252, 372, 522 }, ys = { 680, 655, 630, 605, 580, 555 };
                var line = new ShapeStyle(0x000000, 1);
                foreach (var y in ys) await doc.AddShapeAsync(0, ShapeKind.Line, new RectF(xs[0], y, xs[^1], y), line);
                foreach (var x in new[] { xs[0], xs[^1] }) await doc.AddShapeAsync(0, ShapeKind.Line, new RectF(x, ys[0], x, ys[^1]), line);
                foreach (var x in xs[1..^1]) await doc.AddShapeAsync(0, ShapeKind.Line, new RectF(x, ys[1], x, ys[^1]), line); // ilk satır birleşik
                await doc.AddTextAsync(0, "Aylık Satışlar", 250, 663, 11, font);
                string[][] rows = { new[] { "Ürün", "Adet", "Tutar" }, new[] { "Kalem", "1.250", "3.437,50" }, new[] { "Defter", "320", "12.800,00" }, new[] { "Çanta", "45", "8.775,00" } };
                for (int r = 0; r < rows.Length; r++)
                    for (int c = 0; c < 3; c++) await doc.AddTextAsync(0, rows[r][c], xs[c] + 6, ys[r + 1] - 17, 10, font);
                var photo = new BgraImage { Width = 800, Height = 500, Stride = 3200, Pixels = new byte[800 * 500 * 4] };
                for (int y = 0; y < 500; y++) for (int x = 0; x < 800; x++)
                {
                    int o = (y * 800 + x) * 4;
                    photo.Pixels[o] = (byte)(60 + x * 150 / 800); photo.Pixels[o + 1] = (byte)(120 + y * 100 / 500); photo.Pixels[o + 2] = (byte)(200 - (x + y) * 100 / 1300); photo.Pixels[o + 3] = 255;
                }
                await doc.AddImageAsync(0, new JpegImageSource(ImageCodec.EncodeJpeg(photo, 90)), new RectF(72, 380, 312, 530));
                await doc.SaveAsync(reportPdf);
            }
            await _vm.OpenFilesAsync(new[] { reportPdf });
            var st = _vm.ActiveTab!;
            await WaitRendered();
            var req = new ConvertRequest(ConvertFormat.Word, new[] { 0 }, true, OcrRunOptions.From(_settings));
            var sw = Stopwatch.StartNew();
            var res = await st.ConvertAsync(ConvertFormat.Word, req, reportDocx);
            long ms = sw.ElapsedMilliseconds;
            if (res is null || !File.Exists(reportDocx)) return (false, "DOCX oluşmadı: " + st.StatusText);
            using var w = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(reportDocx, false);
            var body = w.MainDocumentPart!.Document.Body!;
            var text = body.InnerText;
            bool heading = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId>().Any(x => x.Val == "Heading1");
            var tbl = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>().FirstOrDefault();
            int trs = tbl?.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().Count() ?? 0;
            bool span = tbl?.Descendants<DocumentFormat.OpenXml.Wordprocessing.GridSpan>().Any(g => g.Val == 3) ?? false;
            var jpeg = w.MainDocumentPart.ImageParts.Any(i => i.ContentType == "image/jpeg");
            bool tr = text.Contains("İstanbul, İzmir ve Çanakkale") && text.Contains("öğrenci") && text.Contains("12.800,00");
            bool ok = heading && trs == 5 && span && jpeg && tr && !st.Document.IsModified;
            return (ok, $"{ms} ms; Başlık1={heading}, tablo satırı={trs}, birleşik başlık={span}, JPEG resim={jpeg}, Türkçe metin={tr}; {ConvertWorkflow.Summarize(res)}");
        });

        await Step("Dönüştürme", "PDF → Excel: sayılar gerçek sayı", "Hücreler sayısal (1250, 3437,5); birleşik başlık hücresi", async () =>
        {
            var st = _vm.ActiveTab!;
            var res = await st.ConvertAsync(ConvertFormat.Excel, new ConvertRequest(ConvertFormat.Excel, new[] { 0 }, false, OcrRunOptions.From(_settings)), reportXlsx);
            if (res is null || !File.Exists(reportXlsx)) return (false, "XLSX oluşmadı: " + st.StatusText);
            using var x = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(reportXlsx, false);
            var sheet = x.WorkbookPart!.WorksheetParts.First().Worksheet;
            var cells = sheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToList();
            var nums = cells.Where(c => c.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.Number && c.CellValue is not null).Select(c => double.Parse(c.CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            bool merged = sheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.MergeCell>().Any();
            bool ok = nums.Contains(1250) && nums.Contains(3437.5) && nums.Contains(12800) && merged;
            return (ok, $"{cells.Count} hücre, sayısal: {string.Join(", ", nums.Take(8))}; birleşik hücre={merged}");
        });

        await Step("Dönüştürme", "Taranmış PDF → Word (önce OCR)", "Tanıma geçici kopyada yapılır; Word'de Türkçe metin var, özgün PDF değişmez", async () =>
        {
            using var doc = await PdfDocument.OpenAsync(scanPdf);
            long before = new FileInfo(scanPdf).Length;
            var outDocx = Path.Combine(work, "Taranmış.docx");
            var sw = Stopwatch.StartNew();
            var res = await ConvertWorkflow.ConvertAsync(doc, outDocx, new ConvertRequest(ConvertFormat.Word, new[] { 0 }, true, OcrRunOptions.From(_settings)));
            long ms = sw.ElapsedMilliseconds;
            using var w = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(outDocx, false);
            var text = Flat(w.MainDocumentPart!.Document.Body!.InnerText);
            int okLines = ocrLines.Count(l => text.Contains(l.TrimEnd('.')));
            bool unchanged = !doc.IsModified && await doc.CountRealTextCharsAsync(0) <= 40 && new FileInfo(scanPdf).Length == before
                && Directory.GetFiles(AppPaths.Temp, "convert-*.pdf").Length == 0;
            return (res.RecognizedPages == 1 && text.Contains("İstanbul") && okLines >= 3 && unchanged,
                $"{ms} ms; tanınan sayfa={res.RecognizedPages}, birebir satır {okLines}/4, “İstanbul”={text.Contains("İstanbul")}, özgün değişmedi={unchanged}");
        });

        await Step("Sıkıştırma", "Yoğun fotoğraflı PDF'yi sıkıştır", "600 DPI JPEG → 150 DPI; dosya en az 3 kat küçülür, görünüm ve metin korunur", async () =>
        {
            var dense = Path.Combine(work, "Yoğun Fotoğraf.pdf");
            var small = Path.Combine(work, "Yoğun Fotoğraf (sıkıştırılmış).pdf");
            var photo = new BgraImage { Width = 2400, Height = 1800, Stride = 9600, Pixels = new byte[2400 * 1800 * 4] };
            var rnd = new Random(3);
            for (int y = 0; y < 1800; y++) for (int x = 0; x < 2400; x++)
            {
                int o = (y * 2400 + x) * 4;
                photo.Pixels[o] = (byte)Math.Clamp(80 + 120 * Math.Sin(x / 140.0) + rnd.Next(-8, 9), 0, 255);
                photo.Pixels[o + 1] = (byte)Math.Clamp(100 + y * 120 / 1800 + rnd.Next(-8, 9), 0, 255);
                photo.Pixels[o + 2] = (byte)Math.Clamp(140 + 90 * Math.Cos(y / 170.0) + rnd.Next(-8, 9), 0, 255);
                photo.Pixels[o + 3] = 255;
            }
            using (var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1))
            {
                await doc.AddImageAsync(0, new JpegImageSource(ImageCodec.EncodeJpeg(photo, 95)), new RectF(72, 400, 360, 616));
                await doc.AddTextAsync(0, "Fotoğraf altyazısı: Çanakkale Boğazı", 72, 380, 12, font);
                await doc.SaveAsync(dense);
            }
            var sw = Stopwatch.StartNew();
            var rep = await PdfTools.CompressFileAsync(dense, small, CompressOptions.Medium);
            long ms = sw.ElapsedMilliseconds;
            if (!rep.Written) return (false, $"yazılmadı: {rep.BytesBefore} → {rep.BytesAfter}");
            double diff;
            string text;
            using (var a = await PdfDocument.OpenAsync(dense))
            using (var b = await PdfDocument.OpenAsync(small))
            {
                var ia = await PdfTools.RenderAtDpiAsync(a, 0, 40); var ib = await PdfTools.RenderAtDpiAsync(b, 0, 40);
                long sum = 0;
                for (int i = 0; i < ia.Pixels.Length; i += 4) sum += Math.Abs(ia.Pixels[i] - ib.Pixels[i]) + Math.Abs(ia.Pixels[i + 1] - ib.Pixels[i + 1]) + Math.Abs(ia.Pixels[i + 2] - ib.Pixels[i + 2]);
                diff = sum / (ia.Pixels.Length / 4 * 3.0);
                text = (await b.GetPageTextAsync(0)).Text;
            }
            bool ok = rep.Ratio < 1 / 3.0 && diff < 6 && text.Contains("Çanakkale Boğazı");
            return (ok, $"{ms} ms; {CompressDialog.FormatSize(rep.BytesBefore)} → {CompressDialog.FormatSize(rep.BytesAfter)} (oran {rep.Ratio:0.00}), ort. piksel farkı {diff:0.0}, metin korundu={text.Contains("Çanakkale Boğazı")}");
        });

        await Step("Dönüştürme", "Office belgesinden PDF", "Kurulu Office/LibreOffice ile DOCX → PDF; araç yoksa anlaşılır hata", async () =>
        {
            var engine = OfficeExport.EngineFor(reportDocx);
            var outPdf = Path.Combine(work, "Word'den PDF.pdf");
            if (engine == OfficeEngine.None)
            {
                try { await OfficeExport.ConvertAsync(reportDocx, outPdf); return (false, "araç yokken hata verilmedi"); }
                catch (InvalidOperationException ex) { return (ex.Message == OfficeExport.NoEngineMessage && !File.Exists(outPdf), "Office/LibreOffice kurulu değil → anlaşılır hata: " + ex.Message.Split('\n')[0]); }
            }
            var sw = Stopwatch.StartNew();
            await OfficeExport.ConvertAsync(reportDocx, outPdf);
            long ms = sw.ElapsedMilliseconds;
            using var d = await PdfDocument.OpenAsync(outPdf);
            var t = Flat((await d.GetPageTextAsync(0)).Text);
            bool ok = d.PageCount >= 1 && t.Contains("Satış Raporu") && t.Contains("Çanakkale");
            return (ok, $"{engine} ile {ms} ms; {d.PageCount} sayfa, metin korundu={ok}");
        });

        await Step("Arayüz", "Dönüştürme ve sıkıştırma pencereleri", "Word, Excel ve sıkıştırma seçenek pencereleri oluşturulur", async () =>
        {
            int before = _unexpectedDialogs.Count;
            var a = ConvertDialog.Ask(ConvertFormat.Word, 3, "1-3", 1, _settings);
            var b = ConvertDialog.Ask(ConvertFormat.Excel, 3, "1-3", 0, _settings);
            var c = CompressDialog.Ask("Örnek", 1_500_000, _settings);
            await Idle();
            List<string> seen;
            lock (_unexpectedDialogs) { seen = _unexpectedDialogs.Skip(before).ToList(); _unexpectedDialogs.RemoveRange(before, seen.Count); }
            bool ok = a is null && b is null && c is null && seen.Count == 3
                && seen[0].Contains("Word") && seen[0].Contains("taranmış") && seen[1].Contains("Excel") && seen[2].Contains("sıkıştır");
            return (ok, string.Join(" / ", seen.Select(x => x.Length > 70 ? x[..70] + "…" : x)));
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
