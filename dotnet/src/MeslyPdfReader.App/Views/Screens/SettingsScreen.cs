using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Data;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Views.Screens;

/// <summary>
/// Afiş 11 — Ayarlar: solda kategoriler (Genel, Görüntüleme, Dönüştürme, OCR, Tarama, Güvenlik, Gelişmiş),
/// sağda seçili kategorinin ayarları. Değişiklikler anında kaydedilir.
/// </summary>
public sealed class SettingsScreen : UserControl
{
    private static readonly (string Key, string Title, string Header, SymbolRegular Icon)[] Categories =
    {
        ("general", "Genel", "Genel Ayarlar", SymbolRegular.Settings24),
        ("view", "Görüntüleme", "Görüntüleme", SymbolRegular.Eye24),
        ("convert", "Dönüştürme", "Dönüştürme", SymbolRegular.ArrowSync24),
        ("ocr", "OCR (Metin Tanıma)", "OCR (Metin Tanıma)", SymbolRegular.ScanText24),
        ("scan", "Tarama", "Tarama", SymbolRegular.Scan24),
        ("security", "Güvenlik", "Güvenlik", SymbolRegular.ShieldLock24),
        ("advanced", "Gelişmiş", "Gelişmiş", SymbolRegular.WrenchScrewdriver24),
    };

    private readonly StackPanel _content = new() { Margin = new Thickness(28, 20, 28, 28) };
    private readonly TextBlock _header = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Dictionary<string, RadioButton> _cats = new();
    private string _current = "general";
    private MainViewModel? _main;
    private ToggleSwitch? _defaultToggle;

    /// <summary>Öz test için: kategori anahtarları.</summary>
    public static IReadOnlyList<string> CategoryKeys => Categories.Select(c => c.Key).ToList();

    public SettingsScreen()
    {
        var root = new Grid { Margin = new Thickness(28, 20, 28, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var title = new TextBlock { Text = "Ayarlar" };
        title.SetResourceReference(StyleProperty, "ScreenTitle");
        root.Children.Add(title);

        var card = new Border { Padding = new Thickness(0) };
        card.SetResourceReference(StyleProperty, "Card");
        Grid.SetRow(card, 1);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(10, 12, 10, 12) };
        foreach (var c in Categories)
        {
            var rb = new RadioButton { Content = c.Title, Tag = c.Icon.ToString(), GroupName = "settingsCat", Margin = new Thickness(0, 1, 0, 1) };
            rb.SetResourceReference(StyleProperty, "CategoryItem");
            var key = c.Key;
            rb.Checked += (_, _) => Show(key);
            _cats[key] = rb;
            left.Children.Add(rb);
        }
        var leftBorder = new Border { Child = left, BorderThickness = new Thickness(0, 0, 1, 0) };
        leftBorder.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        body.Children.Add(leftBorder);
        var right = new StackPanel { Margin = new Thickness(0) };
        right.Children.Add(_content);
        var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        card.Child = body;
        root.Children.Add(card);
        Content = root;

        DataContextChanged += (_, _) => { _main = DataContext as MainViewModel; if (_main is not null) Show(_current); };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _main is not null) Show(_current);
        };
        // Windows ayarlarından dönünce varsayılan uygulama durumunu yenile
        Application.Current.Activated += (_, _) => { if (IsVisible && _defaultToggle is not null) SetToggleSilently(_defaultToggle, DefaultAppService.IsDefault()); };
        _cats["general"].IsChecked = true;
    }

    /// <summary>Kategoriye geç (öz test ve iç kullanım).</summary>
    public void Select(string key) { if (_cats.TryGetValue(key, out var rb)) { rb.IsChecked = true; Show(key); } }
    public string CurrentCategory => _current;
    public int VisibleSettingCount => _content.Children.Count;

    private void Show(string key)
    {
        _current = key;
        if (_main is null) return;
        var s = _main.Settings;
        _content.Children.Clear();
        _header.Text = Categories.First(c => c.Key == key).Header;
        _content.Children.Add(_header);
        _defaultToggle = null;
        switch (key)
        {
            case "general": General(s); break;
            case "view": View(s); break;
            case "convert": Convert(s); break;
            case "ocr": Ocr(s); break;
            case "scan": Scan(s); break;
            case "security": Security(s); break;
            default: Advanced(s); break;
        }
    }

    // ─────────────── Kategoriler ───────────────

    private void General(AppSettings s)
    {
        Add(Setting("Uygulama başlangıcında Ana Sayfa'yı göster", "Kapalıysa uygulama, son kapattığınızda açık olan belgelerle başlar.", Toggle(s.StartupHome, v => s.StartupHome = v)));
        var counts = new[] { 5, 10, 20, 30, 50, 100 };
        Add(Setting("Son açılan belgeleri hatırla (en fazla)", null, Choice(counts.Select(c => c.ToString()).ToArray(), Math.Max(0, Array.IndexOf(counts, s.RecentLimit) is var i && i >= 0 ? i : 2), i => { s.RecentLimit = counts[i]; _main!.LoadRecent(); })));
        _defaultToggle = Toggle(DefaultAppService.IsDefault(), _ => { });
        _defaultToggle.Click += (_, _) =>
        {
            // Windows varsayılan uygulamayı yalnız kendi ayarlarından değiştirmeye izin verir: anahtar gerçek durumu gösterir.
            SetToggleSilently(_defaultToggle, DefaultAppService.IsDefault());
            DefaultAppService.OpenSettings();
        };
        Add(Setting("Varsayılan PDF görüntüleyici olarak ayarla", "Windows bu seçimi yalnızca kendi Ayarlar uygulamasından yapmanıza izin verir. Anahtara tıklayınca açılan sayfada .pdf için “Mesly PDF Reader”ı seçin; anahtar gerçek durumu gösterir.", _defaultToggle));
        var check = new Button { Content = "Şimdi denetle", Margin = new Thickness(10, 0, 0, 0) };
        check.Click += async (_, _) => await _main!.CheckForUpdatesAsync(manual: true);
        Add(Setting("Otomatik güncellemeleri kontrol et", "Açılışta günde bir kez GitHub'daki en son sürüm numarası sorulur; belge veya kişisel bilgi gönderilmez. Yeni sürüm varsa indirip kurmadan önce onayınız istenir.", Form.Row(Toggle(s.UpdateCheck, v => s.UpdateCheck = v), check)));
        Add(new Border { Height = 1, Margin = new Thickness(0, 14, 0, 10) }.WithResource(Border.BackgroundProperty, "MeslyBorderBrush"));
        var lang = Form.Combo(new[] { "Türkçe" }, 0);
        lang.ToolTip = "Arayüz şu an yalnızca Türkçedir.";
        Add(Setting("Dil", "Arayüz şu an yalnızca Türkçedir.", lang));
        var light = new RadioButton { Content = "Açık (Önerilen)", GroupName = "theme", IsChecked = s.Theme != ThemeChoice.Dark, Margin = new Thickness(0, 0, 0, 6) };
        var dark = new RadioButton { Content = "Koyu", GroupName = "theme", IsChecked = s.Theme == ThemeChoice.Dark };
        light.Checked += (_, _) => { s.Theme = ThemeChoice.Light; _main!.Theme.Apply(ThemeChoice.Light); };
        dark.Checked += (_, _) => { s.Theme = ThemeChoice.Dark; _main!.Theme.Apply(ThemeChoice.Dark); };
        var themes = new StackPanel(); themes.Children.Add(light); themes.Children.Add(dark);
        Add(Setting("Tema", null, themes));
        Add(Group("Dosyalar ve geçmiş"));
        Add(Setting("Dosya geçmişini tut", "Son açılan belgeler Ana Sayfa ve Belgelerim'de listelenir.", Toggle(s.KeepHistory, v => { s.KeepHistory = v; _main!.LoadRecent(); })));
        Add(Setting("Son kalınan sayfayı hatırla", "Belge yeniden açıldığında kaldığınız sayfadan devam eder.", Toggle(s.RememberLastPage, v => s.RememberLastPage = v)));
        Add(Setting("Varsayılan açma klasörü", null, FolderPicker(s.DefaultOpenFolder, v => s.DefaultOpenFolder = v)));
        Add(Setting("Varsayılan kaydetme klasörü", null, FolderPicker(s.DefaultSaveFolder, v => s.DefaultSaveFolder = v)));
        var clear = new Button { Content = "Geçmişi temizle" };
        clear.Click += (_, _) =>
        {
            _main!.Database.ClearRecent(); _main.Database.ClearDocumentStates(); _main.LoadRecent(); _main.Library.RefreshStats();
            MessageDialog.Show("Geçmiş temizlendi", "Son dosyalar listesi (sık kullanılanlar hariç) ve hatırlanan sayfa konumları silindi.", MessageKind.Success);
        };
        Add(Setting("Geçmişi ve sayfa konumlarını temizle", null, clear));
        Add(Group("Otomatik kayıt ve kurtarma"));
        Add(Setting("Otomatik kurtarma", "Değiştirilen belgelerin kopyası arka planda saklanır; beklenmedik kapanmada geri yüklenebilir. Özgün dosyalarınız değiştirilmez.", Toggle(s.AutoRecovery, v => s.AutoRecovery = v)));
        Add(Setting("Kurtarma kopyası aralığı (dakika)", null, Num(s.AutoRecoveryMinutes, 1, 60, v => s.AutoRecoveryMinutes = v)));
    }

    private void View(AppSettings s)
    {
        Add(Setting("Varsayılan yakınlaştırma", "Yeni açılan belgelere uygulanır.", Choice(new[] { "Genişliğe sığdır", "Sayfaya sığdır", "Gerçek boyut", "Son kullanılan" }, (int)s.DefaultZoomMode, i => s.DefaultZoomMode = (ZoomModeChoice)i)));
        Add(Setting("Varsayılan sayfa düzeni", null, Choice(new[] { "Sürekli kaydırma", "Tek sayfa", "Çift sayfa (sürekli)", "Çift sayfa" }, (int)s.DefaultLayout, i => s.DefaultLayout = (LayoutChoice)i)));
        Add(Setting("Karanlık okuma modu", "Sayfaları koyu zeminde açık metinle gösterir (belgeyi değiştirmez).", Toggle(s.DarkReading, v => s.DarkReading = v)));
        Add(Setting("Sayfa küçük resimlerini göster", null, Toggle(s.ShowThumbnails, v => s.ShowThumbnails = v)));
        Add(Setting("Sayfa görüntü önbelleği (MB)", "Yüksek değer kaydırmayı hızlandırır, daha çok bellek kullanır.", Num(s.RenderCacheMb, 64, 4096, v => s.RenderCacheMb = v)));
        Add(Setting("Donanım hızlandırma", "Kapatmak bazı ekran kartı sorunlarını çözer. Yeniden başlatınca uygulanır.", Toggle(s.HardwareAcceleration, v => s.HardwareAcceleration = v)));
    }

    private void Convert(AppSettings s)
    {
        Add(Setting("Taranmış sayfaları önce tanı (OCR)", "Metni olmayan sayfalar dönüştürmeden önce bir kopyada tanınır; özgün belge değişmez.", Toggle(s.ConvertOcr, v => s.ConvertOcr = v)));
        Add(Setting("Tabloları algıla", "Çizgili tablolar Word tablosuna / Excel hücrelerine dönüşür.", Toggle(s.ConvertTables, v => s.ConvertTables = v)));
        Add(Setting("Resimleri ekle (Word, PowerPoint)", null, Toggle(s.ConvertImages, v => s.ConvertImages = v)));
        Add(Setting("Excel: her sayfa ayrı çalışma sayfası", null, Toggle(s.ConvertSheetPerPage, v => s.ConvertSheetPerPage = v)));
        Add(Setting("Excel: yalnızca tabloları aktar", null, Toggle(s.ConvertTablesOnly, v => s.ConvertTablesOnly = v)));
        Add(Setting("Bitince dosyayı aç", "Tek belge dönüştürmede oluşan dosya varsayılan uygulamayla açılır.", Toggle(s.ConvertOpenAfter, v => s.ConvertOpenAfter = v)));
        Add(Setting("Resim çözünürlüğü (DPI)", "PDF → Resim dönüştürmede.", Choice(new[] { "96", "150", "200", "300", "600" }, Array.IndexOf(new[] { 96, 150, 200, 300, 600 }, s.ExportDpi) is var di && di >= 0 ? di : 1, i => s.ExportDpi = new[] { 96, 150, 200, 300, 600 }[i])));
        Add(Setting("Toplu dönüştürme çıktı klasörü", "Boşsa her dosya kendi klasörüne yazılır.", FolderPicker(s.BatchOutputFolder, v => s.BatchOutputFolder = v, "Kaynak dosyanın klasörü")));
        Add(Setting("Sıkıştırma düzeyi", null, Choice(new[] { "Yüksek kalite (220 DPI)", "Dengeli (150 DPI)", "En küçük dosya (100 DPI)" }, s.CompressLevel, i => s.CompressLevel = i)));
        Add(Setting("Sıkıştırırken görselleri gri tonlamaya çevir", null, Toggle(s.CompressGray, v => s.CompressGray = v)));
    }

    private static readonly string[] LangCodes = { "tur+eng", "tur", "eng" };

    private void Ocr(AppSettings s)
    {
        Add(Setting("Belge dili", null, Choice(new[] { "Türkçe + İngilizce", "Türkçe", "İngilizce" }, Math.Max(0, Array.IndexOf(LangCodes, s.OcrLanguages)), i => s.OcrLanguages = LangCodes[i])));
        Add(Setting("Tanıma çözünürlüğü", null, Choice(new[] { "200 DPI (hızlı)", "300 DPI (önerilen)", "400 DPI (küçük yazılar)" }, s.OcrDpi switch { <= 200 => 0, >= 400 => 2, _ => 1 }, i => s.OcrDpi = i switch { 0 => 200, 2 => 400, _ => 300 })));
        Add(Setting("Taranmış belgeleri otomatik tanı ve düzelt", "Metni olmayan taranmış sayfalar belge açılınca arka planda düzeltilir ve tanınır. Ctrl+Z ile tek adımda geri alınır.", Toggle(s.OcrAutoOnOpen, v => s.OcrAutoOnOpen = v)));
        Add(Setting("Sayfa yapısını koru", "OCR sonucu düzenlenirken başlık, paragraf ve tablolar korunur.", Toggle(s.OcrKeepLayout, v => s.OcrKeepLayout = v)));
        Add(Setting("Sayfa yönünü düzelt", "Yan veya ters taranmış sayfalar dik çevrilir.", Toggle(s.OcrAutoRotate, v => s.OcrAutoRotate = v)));
        Add(Setting("Otomatik eğiklik düzelt", "Eğri taranmış satırlar yataya getirilir.", Toggle(s.OcrDeskew, v => s.OcrDeskew = v)));
        Add(Setting("Gürültü azaltma (daha temiz metin)", "Taramadaki tek tük benekler temizlenir.", Toggle(s.OcrDenoise, v => s.OcrDenoise = v)));
        Add(Setting("Kenar gölgelerini temizle", "Tarayıcı kapağından kalan koyu kenar şeritleri beyazlatılır.", Toggle(s.OcrCleanBorders, v => s.OcrCleanBorders = v)));
        Add(Setting("Arka planı beyazlat", "Sararmış veya gri kâğıt beyaza çekilir.", Toggle(s.OcrWhiten, v => s.OcrWhiten = v)));
    }

    private void Scan(AppSettings s)
    {
        Add(Setting("Renk", null, Choice(new[] { "Renkli", "Gri tonlama", "Siyah-beyaz" }, s.ScanColor, i => s.ScanColor = i)));
        Add(Setting("Çözünürlük", null, Choice(new[] { "150 dpi", "200 dpi", "300 dpi", "600 dpi" }, Array.IndexOf(new[] { 150, 200, 300, 600 }, s.ScanDpi) is var si && si >= 0 ? si : 2, i => s.ScanDpi = new[] { 150, 200, 300, 600 }[i])));
        Add(Setting("Dosya türü", null, Choice(new[] { "PDF (Önerilen)", "JPG", "PNG" }, s.ScanFileType, i => s.ScanFileType = i)));
        Add(Setting("Otomatik kırpma", "Tarayıcı camının boş kısımları kesilir, yalnız kâğıt kalır.", Toggle(s.ScanAutoCrop, v => s.ScanAutoCrop = v)));
        Add(Setting("Sayfaları otomatik hizala (düzelt)", "Yan/ters sayfalar dik çevrilir, eğiklik giderilir.", Toggle(s.ScanAutoCorrect, v => s.ScanAutoCorrect = v)));
        Add(Setting("Taramadan sonra metni tanı (OCR)", null, Toggle(s.ScanOcr, v => s.ScanOcr = v)));
        Add(Setting("Kâğıt kaynağı", "Düz cam: her taramada camdaki bir sayfa. Belge besleyici: besleyicideki tüm kâğıtlar tek seferde; çift taraflıda iki yüz de taranır.", Choice(new[] { "Düz cam", "Belge besleyici", "Belge besleyici (çift taraflı)" }, Math.Clamp(s.ScanSource, 0, 2), i => { s.ScanSource = i; s.ScanFeeder = i > 0; })));
    }

    private void Security(AppSettings s)
    {
        Add(Setting("Dış bağlantıları açmadan önce sor", "PDF içindeki web bağlantıları tarayıcıda açılmadan önce onay istenir.", Toggle(s.ConfirmExternalLinks, v => s.ConfirmExternalLinks = v)));
        Add(Setting("Belgeleriniz bu bilgisayarda kalır", "Mesly PDF Reader belgelerinizi hiçbir zaman internete göndermez: OCR, dönüştürme, sıkıştırma ve düzenleme tamamen bu bilgisayarda yapılır. İnternete yalnızca (açıksa) güncelleme denetimi için sürüm numarası sorulur.", Icon(SymbolRegular.CloudOff24)));
        Add(Setting("Gezgin sağ tık menüsü", "Kurulum, PDF dosyalarına “Mesly PDF Reader ile aç” komutunu ekler.", Icon(SymbolRegular.CheckmarkCircle24)));
    }

    private void Advanced(AppSettings s)
    {
        var tempInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        void RefreshTemp() => tempInfo.Text = RecentItem.FormatSize(AppPaths.TempSize());
        RefreshTemp();
        var cleanBtn = new Button { Content = "Şimdi temizle" };
        cleanBtn.Click += (_, _) => { int n = AppPaths.CleanTemp(0, all: true); RefreshTemp(); MessageDialog.Show("Geçici dosyalar", $"{n} geçici dosya silindi. Kullanımdaki dosyalar korunur.", MessageKind.Success); };
        Add(Setting("Geçici dosyalar", AppPaths.Temp, Form.Row(tempInfo, cleanBtn)));
        Add(Setting("Geçici dosyaları sakla (gün)", "Daha eski geçici dosyalar açılışta silinir.", Num(s.TempRetentionDays, 1, 365, v => s.TempRetentionDays = v)));
        var logs = new Button { Content = "Klasörü aç" };
        logs.Click += (_, _) => Open(AppPaths.Logs);
        Add(Setting("Günlük dosyaları", AppPaths.Logs, logs));
        var reset = new Button { Content = "Varsayılana döndür" };
        reset.Click += (_, _) =>
        {
            if (!MessageDialog.Confirm("Ayarları sıfırla", "Tüm ayarlar varsayılan değerlerine döndürülecek. Belgeleriniz, geçmişiniz ve sık kullanılanlarınız etkilenmez.", "Sıfırla", "Vazgeç", MessageKind.Warning)) return;
            s.ResetAll();
            _main!.Theme.ApplyFromSettings();
            Show(_current);
            MessageDialog.Show("Ayarlar sıfırlandı", "Bazı ayarlar (donanım hızlandırma) uygulama yeniden başlatıldığında geçerli olur.", MessageKind.Success);
        };
        Add(Setting("Tüm ayarları sıfırla", null, reset));
        Add(Group("Klavye kısayolları"));
        var keys = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        keys.ColumnDefinitions.Add(new ColumnDefinition());
        var shortcuts = new (string, string)[]
        {
            ("Ctrl+O", "Dosya aç"), ("Ctrl+S / Ctrl+Shift+S", "Kaydet / Farklı kaydet"), ("Ctrl+P", "Yazdır"), ("Ctrl+W", "Belgeyi kapat"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Sonraki / önceki belge"), ("Ctrl+F", "Belgede ara"), ("F3 / Shift+F3", "Sonraki / önceki sonuç"),
            ("Ctrl+G", "Sayfaya git"), ("Ctrl++ / Ctrl+-", "Yakınlaştır / uzaklaştır"), ("Ctrl+0 / Ctrl+1 / Ctrl+2", "Sayfaya sığdır / gerçek boyut / genişliğe sığdır"),
            ("Ctrl+E", "PDF Düzenleme kipi"), ("Esc", "Düzenleme kipinden çık / seçimi kaldır"), ("Ctrl+Z / Ctrl+Y", "Geri al / yinele"),
            ("Ctrl+C / Ctrl+A", "Kopyala / tümünü seç"), ("Ctrl+D", "Yer imi ekle"), ("Ctrl+R / Ctrl+Shift+R", "Sayfaları sağa / sola döndür"),
            ("F11", "Tam ekran"), ("Ctrl+Shift+N", "Karanlık okuma"), ("H / V", "El aracı / seçme aracı"), ("F5", "Belgelerim listesini yenile"),
        };
        for (int i = 0; i < shortcuts.Length; i++)
        {
            keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = shortcuts[i].Item1, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 3, 0, 3) };
            var v = new TextBlock { Text = shortcuts[i].Item2, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            keys.Children.Add(k); keys.Children.Add(v);
        }
        Add(keys);
    }

    // ─────────────── Yardımcılar ───────────────

    private void Add(UIElement e) => _content.Children.Add(e);

    private static TextBlock Group(string title) => new() { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 4) };

    private static FrameworkElement Setting(string title, string? desc, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        t.Children.Add(new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (desc is not null) t.Children.Add(DialogBase.Hint(desc));
        g.Children.Add(t);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; if (fe is ComboBox or NumberBox) fe.MinWidth = 190; }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value };
        t.Checked += (_, _) => { if (t.Tag is not "silent") set(true); };
        t.Unchecked += (_, _) => { if (t.Tag is not "silent") set(false); };
        return t;
    }

    private static void SetToggleSilently(ToggleSwitch t, bool v) { t.Tag = "silent"; t.IsChecked = v; t.Tag = null; }

    private static ComboBox Choice(string[] items, int selected, Action<int> set)
    {
        var c = Form.Combo(items, Math.Clamp(selected, 0, items.Length - 1));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); };
        return c;
    }

    private static NumberBox Num(double value, double min, double max, Action<int> set)
    {
        var n = Form.Number(value, min, max);
        n.ValueChanged += (_, _) => { if (n.Value is { } v) set((int)Math.Clamp(v, min, max)); };
        return n;
    }

    private static SymbolIcon Icon(SymbolRegular s)
    {
        var i = new SymbolIcon { Symbol = s, FontSize = 22 };
        i.SetResourceReference(ForegroundProperty, "MeslyAccentBrush");
        return i;
    }

    private static FrameworkElement FolderPicker(string? value, Action<string?> set, string empty = "Belgeler (varsayılan)")
    {
        var tb = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? empty : value, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var b = new Button { Content = "Seç…" };
        var reset = new Button { Content = "Sıfırla", Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => { var f = FileDialogs.PickFolder(initial: value); if (f is not null) { value = f; set(f); tb.Text = f; } };
        reset.Click += (_, _) => { value = null; set(null); tb.Text = empty; };
        return Form.Row(tb, b, reset);
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageDialog.Show("Açılamadı", ex.Message, MessageKind.Error); }
    }
}
