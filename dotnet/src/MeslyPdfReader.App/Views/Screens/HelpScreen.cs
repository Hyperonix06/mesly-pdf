using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Image = System.Windows.Controls.Image;

namespace MeslyPdfReader.App.Views.Screens;

/// <summary>Afiş 12 — Hakkında / Yardım / Lisans.</summary>
public sealed class HelpScreen : UserControl
{
    /// <summary>
    /// Destek e-posta adresi. Boşsa “Destek ile İletişim” GitHub'daki sorun bildirme sayfasını açar
    /// (adres belirlenene kadar uydurma bir adres kullanılmaz).
    /// </summary>
    public const string? SupportEmail = null;
    public const string ProjectUrl = "https://github.com/Hyperonix06/mesly-pdf";
    public const string IssuesUrl = ProjectUrl + "/issues";

    private readonly ContentControl _body = new();
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private MainViewModel? _main;

    public HelpScreen()
    {
        var root = new Grid { Margin = new Thickness(28, 20, 28, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (key, title) in new[] { ("about", "Hakkında"), ("help", "Yardım"), ("license", "Lisans") })
        {
            var rb = new RadioButton { Content = title, GroupName = "helpTab", MinWidth = 120 };
            rb.SetResourceReference(StyleProperty, "UnderlineTab");
            var k = key;
            rb.Checked += (_, _) => Show(k);
            _tabs[key] = rb;
            tabs.Children.Add(rb);
        }
        root.Children.Add(tabs);
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        card.Child = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(card, 1);
        root.Children.Add(card);
        Content = root;
        DataContextChanged += (_, _) => _main = DataContext as MainViewModel;
        _tabs["about"].IsChecked = true;
    }

    public string CurrentTab { get; private set; } = "about";
    public void Select(string key) { if (_tabs.TryGetValue(key, out var t)) { t.IsChecked = true; Show(key); } }

    private void Show(string key)
    {
        CurrentTab = key;
        _body.Content = key switch { "help" => HelpTab(), "license" => LicenseTab(), _ => AboutTab() };
    }

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    public static string Version => typeof(HelpScreen).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ─────────────── Hakkında ───────────────

    private FrameworkElement AboutTab()
    {
        var st = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 28, 24, 28), MaxWidth = 560 };
        var logo = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        logo.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/icon_256.png")), Width = 84, Height = 84 });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var mesly = new TextBlock { Text = "Mesly", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x2A, 0x4A)), Margin = new Thickness(0, -6, 0, -6) };
        mesly.SetResourceReference(FontFamilyProperty, "MeslyDisplayFont");
        var pr = new TextBlock { Text = "— Pdf Reader —", FontSize = 15, FontWeight = FontWeights.SemiBold };
        pr.SetResourceReference(ForegroundProperty, "MeslyAccentBrush");
        name.Children.Add(mesly); name.Children.Add(pr);
        logo.Children.Add(name);
        st.Children.Add(logo);
        st.Children.Add(new TextBlock { Text = "Mesly Pdf Reader", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 4) });
        st.Children.Add(new TextBlock { Text = $"Sürüm: {Version} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})", HorizontalAlignment = HorizontalAlignment.Center });
        st.Children.Add(new TextBlock { Text = "Profesyonel PDF Okuma, Düzenleme, Dönüştürme ve OCR (Metin Tanıma) Çözümü", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });

        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        badges.Children.Add(Badge("Windows 10", !IsWindows11));
        badges.Children.Add(Badge("Windows 11", IsWindows11));
        st.Children.Add(badges);
        st.Children.Add(new TextBlock
        {
            Text = $"Bu bilgisayar: {(IsWindows11 ? "Windows 11" : "Windows 10")} (yapı {Environment.OSVersion.Version.Build})",
            FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        }.WithResource(ForegroundProperty, "MeslyMutedTextBrush"));

        var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        links.Children.Add(Link("Lisans Sözleşmesi", () => ShowDocument("Lisans Sözleşmesi", LegalTexts.Eula)));
        links.Children.Add(Link("Gizlilik Politikası", () => ShowDocument("Gizlilik Politikası", LegalTexts.Privacy)));
        links.Children.Add(Link("Destek ile İletişim", ContactSupport));
        st.Children.Add(links);

        var upd = new Button { Content = "Güncellemeleri denetle", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        upd.Click += async (_, _) => { if (_main is not null) await _main.CheckForUpdatesAsync(manual: true); };
        st.Children.Add(upd);
        st.Children.Add(new TextBlock { Text = "© Mesly. Tüm hakları saklıdır.", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) }.WithResource(ForegroundProperty, "MeslyMutedTextBrush"));
        return st;
    }

    private static Border Badge(string text, bool current)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Viewbox { Width = 26, Height = 26, Child = WindowsLogo() };
        sp.Children.Add(icon);
        var t = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        t.Children.Add(new TextBlock { Text = "Uyumlu", FontSize = 12 });
        sp.Children.Add(t);
        var b = new Border { Child = sp, Padding = new Thickness(14, 8, 18, 8), Margin = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(current ? 2 : 1) };
        b.SetResourceReference(Border.BorderBrushProperty, current ? "MeslyAccentBrush" : "MeslyBorderBrush");
        b.SetResourceReference(Border.BackgroundProperty, "MeslyPanelBrush");
        b.ToolTip = current ? "Şu an bu sürümde çalışıyor" : null;
        return b;
    }

    /// <summary>Dört kareli Windows simgesi (çizimle).</summary>
    private static Canvas WindowsLogo()
    {
        var c = new Canvas { Width = 20, Height = 20 };
        var brush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        foreach (var (x, y) in new[] { (0, 0), (10.5, 0), (0, 10.5), (10.5, 10.5) })
        {
            var r = new System.Windows.Shapes.Rectangle { Width = 9.5, Height = 9.5, Fill = brush };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            c.Children.Add(r);
        }
        return c;
    }

    private static TextBlock Link(string text, Action click)
    {
        var h = new Hyperlink(new Run(text));
        h.SetResourceReference(TextElement.ForegroundProperty, "MeslyAccentBrush");
        h.Click += (_, _) => click();
        return new TextBlock(h) { Margin = new Thickness(14, 0, 14, 0), FontSize = 13 };
    }

    public static void ContactSupport()
    {
        if (!string.IsNullOrWhiteSpace(SupportEmail))
            OpenUrl($"mailto:{SupportEmail}?subject={Uri.EscapeDataString($"Mesly Pdf Reader {Version} - Destek")}");
        else OpenUrl(IssuesUrl);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageDialog.Show("Açılamadı", $"{url}\n\n{ex.Message}", MessageKind.Error); }
    }

    private static void ShowDocument(string title, string text) => TextDocumentDialog.Show(title, text);

    // ─────────────── Yardım ───────────────

    private FrameworkElement HelpTab()
    {
        var st = new StackPanel { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        st.Children.Add(H("Hızlı başlangıç"));
        foreach (var (t, d) in HelpTopics)
        {
            st.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 12, 0, 2) });
            st.Children.Add(new TextBlock { Text = d, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        }
        st.Children.Add(H("Sorun giderme"));
        st.Children.Add(new TextBlock { Text = "Bir hata ile karşılaşırsanız günlük dosyaları sorunu anlamamıza yardımcı olur. Günlükler yalnızca bu bilgisayarda tutulur; siz göndermedikçe kimseyle paylaşılmaz.", TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var logs = new Button { Content = "Günlük klasörünü aç" };
        logs.Click += (_, _) => OpenUrl(AppPaths.Logs);
        var support = new Button { Content = "Destek ile İletişim", Margin = new Thickness(8, 0, 0, 0) };
        support.Click += (_, _) => ContactSupport();
        var keys = new Button { Content = "Klavye kısayolları", Margin = new Thickness(8, 0, 0, 0) };
        keys.Click += (_, _) => { _main?.Navigate(AppScreen.Settings); Application.Current.Dispatcher.BeginInvoke(() => (Window.GetWindow(this) as MainWindow)?.SettingsScreenControl.Select("advanced")); };
        row.Children.Add(logs); row.Children.Add(support); row.Children.Add(keys);
        st.Children.Add(row);
        return st;
    }

    public static readonly (string Title, string Text)[] HelpTopics =
    {
        ("PDF açma", "Sol menüden “PDF Aç”a tıklayın veya dosyayı pencereye sürükleyin. Belgelerim ekranında son kullanılanlar, bilgisayarınızdaki tüm PDF'ler ve sık kullanılanlarınız listelenir; yıldız simgesiyle bir belgeyi sık kullanılanlara ekleyebilirsiniz."),
        ("Okuma ve gezinme", "Görüntüleyicide soldaki küçük resimlerden sayfaya gidin, şeritteki Giriş sekmesinden yakınlaştırın, döndürün veya arayın. Birden çok belge açıkken üstteki belge sekmeleriyle geçiş yapılır."),
        ("PDF düzenleme", "Araçlar › PDF Düzenle (Ctrl+E) ile düzenleme kipine geçin. Metne tıklayıp doğrudan yazın; özgün yazı tipi, hizalama ve satır düzeni korunur. Vurgulama, altını/üstünü çizme, açıklama notu, imza, şekil, resim ve bağlantı ekleyebilirsiniz. Her işlem Ctrl+Z ile geri alınır."),
        ("Dönüştürme", "Dönüştür ekranından PDF'yi Word, Excel, PowerPoint, resim veya metne çevirin. Çok sayıda dosya için Toplu Dönüştürme kuyruğunu kullanın. Office belgelerini PDF'ye çevirmek için dosyayı açmanız yeterlidir (Microsoft Office veya LibreOffice gerekir)."),
        ("OCR (Metin Tanıma)", "Taranmış PDF'ler açıldığında metin otomatik tanınır ve sayfalar düzeltilir. OCR ekranında kaynak dosyayı, dili ve çıktı türünü (aranabilir PDF, düzenlenebilir metin, Word, metin dosyası) seçip tanımayı başlatabilirsiniz. Tanıma tamamen bu bilgisayarda yapılır."),
        ("Tarama", "Tarama ekranında tarayıcınızı, rengi ve çözünürlüğü seçin; yeni PDF oluşturun, açık belgeye ekleyin veya resim olarak kaydedin. Otomatik kırpma ve hizalama kâğıdı düzgün hale getirir."),
        ("Sayfa yönetimi", "Araçlar ekranında belgeleri birleştirin, sayfa aralıklarına bölün veya sayfaları sürükleyerek yeniden sıralayın. “Uygula” ile değişiklikler belgeye işlenir; kaydetmeden önce “Sıfırla” ile vazgeçebilirsiniz."),
    };

    private static TextBlock H(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };

    // ─────────────── Lisans ───────────────

    private FrameworkElement LicenseTab()
    {
        var st = new StackPanel { Margin = new Thickness(28, 22, 28, 28) };
        st.Children.Add(H("Lisans"));
        st.Children.Add(new TextBlock { Text = "Mesly Pdf Reader, Lisans Sözleşmesi koşullarıyla kullanılır. Uygulama aşağıdaki açık kaynak bileşenleri kendi lisanslarına uygun olarak içerir; hiçbiri kaynak kodu açma zorunluluğu getirmez.", TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        var eula = new Button { Content = "Lisans Sözleşmesi" };
        eula.Click += (_, _) => ShowDocument("Lisans Sözleşmesi", LegalTexts.Eula);
        var priv = new Button { Content = "Gizlilik Politikası", Margin = new Thickness(8, 0, 0, 0) };
        priv.Click += (_, _) => ShowDocument("Gizlilik Politikası", LegalTexts.Privacy);
        var folder = new Button { Content = "Lisans dosyaları klasörü", Margin = new Thickness(8, 0, 0, 0) };
        folder.Click += (_, _) => OpenUrl(Path.Combine(AppContext.BaseDirectory, "Licenses"));
        row.Children.Add(eula); row.Children.Add(priv); row.Children.Add(folder);
        st.Children.Add(row);
        st.Children.Add(H("Üçüncü taraf bileşenler"));
        var rows = ThirdPartyRows();
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        void Cell(string text, int r, int c, bool bold = false)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 12, 4), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12.5 };
            Grid.SetRow(t, r); Grid.SetColumn(t, c); grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell("Bileşen", 0, 0, true); Cell("Kullanım", 0, 1, true); Cell("Lisans", 0, 2, true);
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rows[i].Name, i + 1, 0); Cell(rows[i].Use, i + 1, 1); Cell(rows[i].License, i + 1, 2);
        }
        st.Children.Add(grid);
        if (rows.Count == 0) st.Children.Add(DialogBase.Hint("Lisans dosyası (Licenses\\THIRD_PARTY_NOTICES.md) bulunamadı."));
        return st;
    }

    public static List<(string Name, string Use, string License)> ThirdPartyRows()
    {
        var list = new List<(string, string, string)>();
        var path = Path.Combine(AppContext.BaseDirectory, "Licenses", "THIRD_PARTY_NOTICES.md");
        if (!File.Exists(path)) return list;
        bool inFirstTable = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("| Bileşen | Sürüm")) { inFirstTable = true; continue; }
            if (!inFirstTable) continue;
            if (!line.StartsWith('|')) { if (list.Count > 0) break; continue; }
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 4 || cells[0].StartsWith("---")) continue;
            list.Add((cells[0], cells[2], cells[3]));
        }
        return list;
    }
}

/// <summary>Uzun metin gösteren basit, kaydırılabilir pencere.</summary>
public sealed class TextDocumentDialog : DialogBase
{
    private TextDocumentDialog(string title, string text) : base(title, 640)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 440, FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        };
        Body = box;
        AddButton("Kapat", () => DialogResult = true, primary: true, isCancel: true, isDefault: true);
    }

    public static void Show(string title, string text) => new TextDocumentDialog(title, text).ShowDialog();
}
