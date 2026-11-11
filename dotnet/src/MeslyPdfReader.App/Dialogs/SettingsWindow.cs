using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Data;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>Ayarlar: görünüm, dosyalar, performans, geçici dosyalar, Windows entegrasyonu, kısayollar, hakkında.</summary>
public sealed class SettingsWindow : FluentWindow
{
    private readonly AppSettings _s;
    private readonly MainViewModel _main;
    private readonly AppDatabase _db;
    private readonly StackPanel _content = new() { Margin = new Thickness(28, 6, 28, 28), MaxWidth = 760 };

    public static void ShowFor(MainViewModel main, AppSettings s, AppDatabase db) => new SettingsWindow(main, s, db).ShowDialog();

    /// <summary>Öz test için modsuz pencere.</summary>
    public static Window CreateForTest(MainViewModel main, AppSettings s, AppDatabase db) => new SettingsWindow(main, s, db);

    private SettingsWindow(MainViewModel main, AppSettings s, AppDatabase db)
    {
        _main = main; _s = s; _db = db;
        Title = "Ayarlar";
        Width = 860; Height = 760; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow is { IsLoaded: true } mw && mw != this) Owner = mw;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        SetResourceReference(BackgroundProperty, "MeslyChromeBrush");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TitleBar { Title = "Ayarlar — Mesly PDF Reader", ShowMinimize = false });
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
        Build();
    }

    private void Section(string title, SymbolRegular icon, params UIElement[] items)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 8) };
        var ic = new SymbolIcon { Symbol = icon, FontSize = 20, Margin = new Thickness(0, 0, 10, 0) };
        ic.SetResourceReference(ForegroundProperty, "MeslyAccentBrush");
        header.Children.Add(ic);
        header.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        _content.Children.Add(header);
        var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 8, 18, 14), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "MeslyPanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var sp = new StackPanel();
        foreach (var i in items) sp.Children.Add(i);
        card.Child = sp;
        _content.Children.Add(card);
    }

    private static FrameworkElement Setting(string title, string? desc, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        t.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        if (desc is not null) t.Children.Add(DialogBase.Hint(desc));
        g.Children.Add(t);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; if (fe is ComboBox or NumberBox) fe.MinWidth = 200; }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value };
        t.Checked += (_, _) => set(true);
        t.Unchecked += (_, _) => set(false);
        return t;
    }

    private static ComboBox Choice(string[] items, int selected, Action<int> set)
    {
        var c = Form.Combo(items, selected);
        c.SelectionChanged += (_, _) => set(c.SelectedIndex);
        return c;
    }

    private static NumberBox Num(double value, double min, double max, Action<int> set)
    {
        var n = Form.Number(value, min, max);
        n.ValueChanged += (_, _) => { if (n.Value is { } v) set((int)Math.Clamp(v, min, max)); };
        return n;
    }

    private void Build()
    {
        Section("Görünüm", SymbolRegular.DarkTheme24,
            Setting("Tema", "Sistem, Windows'un açık/koyu ayarını izler.", Choice(new[] { "Sistem", "Açık", "Koyu" }, (int)_s.Theme, i => { _s.Theme = (ThemeChoice)i; _main.Theme.Apply(_s.Theme); })),
            Setting("Arayüz dili", "Bu sürümde arayüz yalnızca Türkçedir.", new TextBlock { Text = "Türkçe", FontWeight = FontWeights.SemiBold }),
            Setting("Varsayılan yakınlaştırma", "Yeni açılan belgelere uygulanır.", Choice(new[] { "Genişliğe sığdır", "Sayfaya sığdır", "Gerçek boyut", "Son kullanılan" }, (int)_s.DefaultZoomMode, i => _s.DefaultZoomMode = (ZoomModeChoice)i)),
            Setting("Varsayılan sayfa düzeni", null, Choice(new[] { "Sürekli kaydırma", "Tek sayfa", "Çift sayfa (sürekli)", "Çift sayfa" }, (int)_s.DefaultLayout, i => _s.DefaultLayout = (LayoutChoice)i)),
            Setting("Karanlık okuma modu", "Sayfaları koyu zeminde açık metinle gösterir (belgeyi değiştirmez).", Toggle(_s.DarkReading, v => _s.DarkReading = v)),
            Setting("Sayfa küçük resimlerini göster", null, Toggle(_s.ShowThumbnails, v => _s.ShowThumbnails = v)));

        var openFolder = FolderPicker(_s.DefaultOpenFolder, v => _s.DefaultOpenFolder = v);
        var saveFolder = FolderPicker(_s.DefaultSaveFolder, v => _s.DefaultSaveFolder = v);
        var clearHistory = new Button { Content = "Geçmişi temizle" };
        clearHistory.Click += (_, _) => { _db.ClearRecent(); _db.ClearDocumentStates(); _main.LoadRecent(); MessageDialog.Show("Geçmiş temizlendi", "Son dosyalar listesi ve hatırlanan sayfa konumları silindi.", MessageKind.Success); };
        Section("Dosyalar ve geçmiş", SymbolRegular.History24,
            Setting("Dosya geçmişini tut", "Son açılan belgeler ana ekranda listelenir.", Toggle(_s.KeepHistory, v => _s.KeepHistory = v)),
            Setting("Listelenecek son dosya sayısı", null, Num(_s.RecentLimit, 5, 100, v => _s.RecentLimit = v)),
            Setting("Son kalınan sayfayı hatırla", "Belge yeniden açıldığında kaldığınız sayfadan devam eder.", Toggle(_s.RememberLastPage, v => _s.RememberLastPage = v)),
            Setting("Varsayılan açma klasörü", null, openFolder),
            Setting("Varsayılan kaydetme klasörü", null, saveFolder),
            Setting("Geçmişi ve sayfa konumlarını temizle", null, clearHistory));

        Section("Otomatik kayıt ve kurtarma", SymbolRegular.SaveEdit24,
            Setting("Otomatik kurtarma", "Değiştirilen belgelerin kopyası arka planda saklanır; beklenmedik kapanmada geri yüklenebilir. Özgün dosyalarınız değiştirilmez.", Toggle(_s.AutoRecovery, v => _s.AutoRecovery = v)),
            Setting("Kurtarma kopyası aralığı (dakika)", null, Num(_s.AutoRecoveryMinutes, 1, 60, v => _s.AutoRecoveryMinutes = v)));

        var tempInfo = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        void RefreshTemp() => tempInfo.Text = RecentItem.FormatSize(AppPaths.TempSize());
        RefreshTemp();
        var cleanBtn = new Button { Content = "Şimdi temizle" };
        cleanBtn.Click += (_, _) => { int n = AppPaths.CleanTemp(0, all: true); RefreshTemp(); MessageDialog.Show("Geçici dosyalar", $"{n} geçici dosya silindi. Kullanımdaki dosyalar korunur.", MessageKind.Success); };
        var tempRow = Form.Row(tempInfo, cleanBtn);
        Section("Performans ve geçici dosyalar", SymbolRegular.TopSpeed24,
            Setting("Sayfa görüntü önbelleği (MB)", "Yüksek değer kaydırmayı hızlandırır, daha çok bellek kullanır.", Num(_s.RenderCacheMb, 64, 4096, v => _s.RenderCacheMb = v)),
            Setting("Donanım hızlandırma", "Kapatmak bazı ekran kartı sorunlarını çözer. Yeniden başlatınca uygulanır.", Toggle(_s.HardwareAcceleration, v => _s.HardwareAcceleration = v)),
            Setting("Geçici dosyaları sakla (gün)", "Daha eski geçici dosyalar açılışta silinir.", Num(_s.TempRetentionDays, 1, 365, v => _s.TempRetentionDays = v)),
            Setting("Geçici dosyalar", AppPaths.Temp, tempRow));

        Section("Güvenlik", SymbolRegular.ShieldLock24,
            Setting("Dış bağlantıları açmadan önce sor", "PDF içindeki web bağlantıları tarayıcıda açılmadan önce onay istenir.", Toggle(_s.ConfirmExternalLinks, v => _s.ConfirmExternalLinks = v)),
            Setting("Çevrimdışı çalışma", "Mesly PDF Reader belgelerinizi hiçbir zaman internete göndermez. Bu sürümdeki tüm özellikler çevrimdışı çalışır.", new SymbolIcon { Symbol = SymbolRegular.CloudOff24, FontSize = 22 }));

        var defaultApp = new Button { Content = "Varsayılan uygulamalar…" };
        defaultApp.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { } };
        Section("Windows entegrasyonu", SymbolRegular.Window24,
            Setting("PDF dosyalarını varsayılan olarak Mesly ile aç", "Windows, varsayılan uygulama seçimini yalnızca Ayarlar uygulamasından yapmanıza izin verir. Açılan sayfada .pdf için “Mesly PDF Reader”ı seçin.", defaultApp),
            Setting("Gezgin sağ tık menüsü", "Kurulum, PDF dosyalarına “Mesly PDF Reader ile aç” komutunu ekler.", new SymbolIcon { Symbol = SymbolRegular.CheckmarkCircle24, FontSize = 22 }));

        var keys = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        keys.ColumnDefinitions.Add(new ColumnDefinition());
        var shortcuts = new (string, string)[]
        {
            ("Ctrl+O", "Dosya aç"), ("Ctrl+S / Ctrl+Shift+S", "Kaydet / Farklı kaydet"), ("Ctrl+P", "Yazdır"), ("Ctrl+W", "Sekmeyi kapat"),
            ("Ctrl+Tab / Ctrl+Shift+Tab", "Sonraki / önceki sekme"), ("Ctrl+F", "Belgede ara"), ("F3 / Shift+F3", "Sonraki / önceki sonuç"),
            ("Ctrl+G", "Sayfaya git"), ("Ctrl++ / Ctrl+-", "Yakınlaştır / uzaklaştır"), ("Ctrl+0 / Ctrl+1", "Sayfaya sığdır / gerçek boyut"),
            ("Ctrl+2", "Genişliğe sığdır"), ("Ctrl+Fare tekerleği", "İmleç etrafında yakınlaştır"), ("PgUp / PgDn, Home / End", "Sayfa gezinme"),
            ("Ctrl+C / Ctrl+A", "Kopyala / tümünü seç"), ("Ctrl+Z / Ctrl+Y", "Geri al / yinele"), ("Ctrl+D", "Yer imi ekle"),
            ("Ctrl+R / Ctrl+Shift+R", "Sayfaları sağa / sola döndür"), ("Delete", "Seçili sayfaları sil (küçük resimlerde)"),
            ("F11", "Tam ekran"), ("Ctrl+Shift+N", "Karanlık okuma modu"), ("H / V", "El aracı / seçim aracı"),
        };
        for (int i = 0; i < shortcuts.Length; i++)
        {
            keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = shortcuts[i].Item1, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 3, 0, 3) };
            var v = new TextBlock { Text = shortcuts[i].Item2, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            keys.Children.Add(k); keys.Children.Add(v);
        }
        Section("Klavye kısayolları", SymbolRegular.Keyboard24, keys);

        var ver = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3);
        var about = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        head.Children.Add(new System.Windows.Controls.Image { Source = ImageCodec.LoadAppImage("Assets/icon_256.png"), Width = 56, Height = 56, Margin = new Thickness(0, 0, 14, 0) });
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(new TextBlock { Text = "Mesly PDF Reader", FontSize = 20, FontWeight = FontWeights.SemiBold });
        ht.Children.Add(DialogBase.Hint($"Sürüm {ver} · .NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · {Environment.OSVersion.VersionString}"));
        head.Children.Add(ht);
        about.Children.Add(head);
        about.Children.Add(DialogBase.Hint("© 2026 Mesly. Bu sürüm: PDF görüntüleme, arama, metin seçme, yer imleri, yazdırma, sayfa yönetimi (birleştirme, bölme, ekleme, silme, taşıma, döndürme, kırpma, boyutlandırma), görüntü/metin dışa aktarma ve görüntülerden PDF."));
        var licenses = new Button { Content = "Lisans ve üçüncü taraf bileşenler", Margin = new Thickness(0, 12, 8, 0) };
        licenses.Click += (_, _) => OpenFile(Path.Combine(AppContext.BaseDirectory, "Licenses", "THIRD_PARTY_NOTICES.md"));
        var logs = new Button { Content = "Günlük klasörünü aç", Margin = new Thickness(0, 12, 0, 0) };
        logs.Click += (_, _) => OpenFile(AppPaths.Logs);
        about.Children.Add(Form.Row(licenses, logs));
        Section("Hakkında", SymbolRegular.Info24, about);
    }

    private static FrameworkElement FolderPicker(string? value, Action<string?> set)
    {
        var tb = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "Belgeler (varsayılan)" : value, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var b = new Button { Content = "Seç…" };
        var reset = new Button { Content = "Sıfırla", Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => { var f = FileDialogs.PickFolder(initial: value); if (f is not null) { value = f; set(f); tb.Text = f; } };
        reset.Click += (_, _) => { value = null; set(null); tb.Text = "Belgeler (varsayılan)"; };
        return Form.Row(tb, b, reset);
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageDialog.Show("Açılamadı", ex.Message, MessageKind.Error); }
    }
}
