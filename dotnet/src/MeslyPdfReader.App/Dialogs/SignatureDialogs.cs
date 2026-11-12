using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using Image = System.Windows.Controls.Image;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>
/// İmza oluşturma: fareyle/kalemle çiz, adınızı yazın veya imzanın fotoğrafını/taramasını seçin. Sonuç saydam zeminli
/// görseldir; sayfaya yerleştirildiğinde alttaki metni örtmez. Bu el yazısı imzadır, nitelikli elektronik imza değildir.
/// </summary>
public sealed class SignatureDialog : DialogBase
{
    public static string Folder => Path.Combine(AppPaths.Root, "signatures");

    /// <summary>Kayıtlı imzalar (en yeni önce).</summary>
    public static IReadOnlyList<string> Saved()
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.png").OrderByDescending(File.GetLastWriteTime).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    public static BgraImage Load(string path) => ImageCodec.Decode(File.ReadAllBytes(path));

    public static string Save(BgraImage img)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"imza-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
        File.WriteAllBytes(path, ImageCodec.EncodePng(img, 96));
        return path;
    }

    private static readonly string[] ScriptFonts = { "Segoe Script", "Ink Free", "Lucida Handwriting", "Segoe Print", "Segoe UI" };
    private static readonly (string Name, Color Color)[] Inks = { ("Siyah", Colors.Black), ("Mavi", Color.FromRgb(0x1A, 0x3D, 0x9C)), ("Kırmızı", Color.FromRgb(0xC6, 0x28, 0x28)) };

    private readonly InkCanvas _ink = new() { Height = 170, Background = Brushes.White };
    private readonly TextBox _typed = new() { PlaceholderText = "Adınız Soyadınız", FontSize = 15 };
    private readonly TextBlock _typedPreview = new() { FontSize = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _imgPreview = new() { Height = 150, Stretch = Stretch.Uniform };
    private readonly CheckBox _save = new() { Content = "Bu imzayı sonraki kullanımlar için kaydet", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TabControl _tabs = new();
    private readonly TextBlock _err = Error();
    private BgraImage? _fromFile;
    private Color _color = Colors.Black;
    private string _font = "Segoe UI";

    public BgraImage? Result { get; private set; }

    private SignatureDialog() : base("İmza oluştur", 560)
    {
        var st = new StackPanel();
        // Kayıtlı imzalar
        var saved = Saved();
        if (saved.Count > 0)
        {
            st.Children.Add(Label("Kayıtlı imzalar (birine tıklayarak kullanın)", 0));
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var f in saved.Take(6))
            {
                var b = new Button { Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 8), Content = new Image { Source = SafeThumb(f), Height = 44, MaxWidth = 150, Stretch = Stretch.Uniform }, ToolTip = "Bu imzayı kullan" };
                var path = f;
                b.Click += (_, _) => { try { Result = Load(path); DialogResult = true; } catch (Exception ex) { ShowError(_err, ex.Message); } };
                wrap.Children.Add(b);
            }
            st.Children.Add(wrap);
        }

        // Çiz
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = _color, Width = 2.6, Height = 2.6, FitToCurve = true, StylusTip = StylusTip.Ellipse };
        var drawBorder = new Border { Child = _ink, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        drawBorder.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var clear = new Button { Content = "Temizle", Margin = new Thickness(0, 8, 0, 0) };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        var drawPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        drawPanel.Children.Add(Hint("Aşağıdaki alana fare, dokunmatik ekran veya kalemle imzanızı atın."));
        drawPanel.Children.Add(drawBorder);
        drawPanel.Children.Add(clear);

        // Yaz
        _typed.TextChanged += (_, _) => _typedPreview.Text = _typed.Text;
        var fonts = Form.Combo(ScriptFonts.Where(f => Fonts.SystemFontFamilies.Any(s => s.Source.Equals(f, StringComparison.OrdinalIgnoreCase))).DefaultIfEmpty("Segoe UI"), 0);
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) { _font = f; _typedPreview.FontFamily = new FontFamily(f); } };
        _font = fonts.SelectedItem as string ?? _font;
        _typedPreview.FontFamily = new FontFamily(_font);
        var typePreviewBox = new Border { Height = 110, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _typedPreview };
        typePreviewBox.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var typePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        typePanel.Children.Add(_typed);
        typePanel.Children.Add(Form.Labeled("Yazı stili", fonts));
        typePanel.Children.Add(typePreviewBox);

        // Resim
        var pick = new Button { Content = "Resim seç…" };
        pick.Click += (_, _) =>
        {
            var f = FileDialogs.OpenImage();
            if (f is null) return;
            try
            {
                _fromFile = RemoveBackground(ImageCodec.Decode(File.ReadAllBytes(f)));
                _imgPreview.Source = ImageCodec.ToBitmapSource(_fromFile);
                ShowError(_err, null);
            }
            catch (Exception ex) { ShowError(_err, "Resim okunamadı: " + ex.Message); }
        };
        var imgBox = new Border { Margin = new Thickness(0, 10, 0, 0), Background = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _imgPreview, MinHeight = 150 };
        imgBox.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var imgPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        imgPanel.Children.Add(Hint("Beyaz kâğıda atılmış imzanızın fotoğrafını veya taramasını seçin; kâğıt zemini otomatik olarak saydam yapılır."));
        imgPanel.Children.Add(pick);
        imgPanel.Children.Add(imgBox);

        _tabs.Items.Add(new TabItem { Header = "Çiz", Content = drawPanel });
        _tabs.Items.Add(new TabItem { Header = "Yaz", Content = typePanel });
        _tabs.Items.Add(new TabItem { Header = "Resim", Content = imgPanel });
        st.Children.Add(_tabs);

        // Mürekkep rengi
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        colors.Children.Add(new TextBlock { Text = "Renk:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var (name, c) in Inks)
        {
            var rb = new RadioButton { Content = name, GroupName = "ink", IsChecked = c == _color, Margin = new Thickness(0, 0, 14, 0) };
            var cc = c;
            rb.Checked += (_, _) =>
            {
                _color = cc;
                _ink.DefaultDrawingAttributes.Color = cc;
                foreach (var s in _ink.Strokes) s.DrawingAttributes.Color = cc;
                _typedPreview.Foreground = new SolidColorBrush(cc);
            };
            colors.Children.Add(rb);
        }
        st.Children.Add(colors);
        st.Children.Add(_save);
        st.Children.Add(Hint("Bu, belgeye yerleştirilen el yazısı imza görüntüsüdür; nitelikli elektronik imza (e-imza) yerine geçmez."));
        st.Children.Add(_err);
        Body = st;
        AddOkCancel("İmzayı yerleştir", Validate);
    }

    private static ImageSource? SafeThumb(string path)
    {
        try { return ImageCodec.ToBitmapSource(Load(path)); } catch { return null; }
    }

    private bool Validate()
    {
        BgraImage? img = _tabs.SelectedIndex switch
        {
            0 => _ink.Strokes.Count > 0 ? RenderInk() : null,
            1 => string.IsNullOrWhiteSpace(_typed.Text) ? null : RenderTyped(_typed.Text.Trim(), _font, _color),
            _ => _fromFile is null ? null : Tint(_fromFile, _color),
        };
        if (img is null)
        {
            ShowError(_err, _tabs.SelectedIndex switch { 0 => "Önce imzanızı çizin.", 1 => "Adınızı yazın.", _ => "Bir imza resmi seçin." });
            return false;
        }
        Result = img;
        if (_save.IsChecked == true)
            try { Save(img); } catch (Exception ex) { Log.Warn("İmza kaydedilemedi: " + ex.Message); }
        return true;
    }

    private BgraImage RenderInk()
    {
        var bounds = _ink.Strokes.GetBounds();
        bounds.Inflate(6, 6);
        const double scale = 3; // yazdırmada keskin kalsın
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            _ink.Strokes.Draw(dc);
        }
        return Rasterize(dv, (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale));
    }

    public static BgraImage RenderTyped(string text, string font, Color color)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 120, new SolidColorBrush(color), 1.0);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(20, 10));
        return Crop(Rasterize(dv, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace + 40), (int)Math.Ceiling(ft.Height + 20)));
    }

    private static BgraImage Rasterize(Visual v, int w, int h)
    {
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        // Pbgra32 → Bgra32 (önceden çarpılmamış): PDF yumuşak maskesi düz alfa bekler.
        return ImageCodec.FromBitmapSource(new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0));
    }

    /// <summary>Kâğıt zeminini saydam yapar: açık pikseller saydam, koyular opak (yumuşak geçişli).</summary>
    public static BgraImage RemoveBackground(BgraImage src)
    {
        var px = (byte[])src.Pixels.Clone();
        // Zemin parlaklığı: en parlak %10'luk dilim
        var lum = new int[256];
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++) { int o = y * src.Stride + x * 4; lum[(px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8]++; }
        long total = (long)src.Width * src.Height, acc = 0; int paper = 255;
        for (int l = 255; l >= 0; l--) { acc += lum[l]; if (acc >= total / 10) { paper = l; break; } }
        int hi = Math.Max(60, paper - 25), lo = Math.Max(0, hi - 90);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int o = y * src.Stride + x * 4;
                int l = (px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8;
                int a = l >= hi ? 0 : l <= lo ? 255 : 255 * (hi - l) / (hi - lo);
                px[o + 3] = (byte)Math.Min(px[o + 3], a);
            }
        return Crop(new BgraImage { Width = src.Width, Height = src.Height, Stride = src.Stride, Pixels = px });
    }

    /// <summary>Opak piksellerin rengini seçilen mürekkep rengine çevirir (siyah seçiliyse özgün ton korunur).</summary>
    private static BgraImage Tint(BgraImage src, Color c)
    {
        if (c == Colors.Black) return src;
        var px = (byte[])src.Pixels.Clone();
        for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; }
        return new BgraImage { Width = src.Width, Height = src.Height, Stride = src.Stride, Pixels = px };
    }

    /// <summary>Saydam kenarları kırpar.</summary>
    public static BgraImage Crop(BgraImage src)
    {
        int x0 = src.Width, y0 = src.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (src.Pixels[y * src.Stride + x * 4 + 3] > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) throw new InvalidOperationException("İmzada görünür çizgi bulunamadı.");
        x0 = Math.Max(0, x0 - 4); y0 = Math.Max(0, y0 - 4); x1 = Math.Min(src.Width - 1, x1 + 4); y1 = Math.Min(src.Height - 1, y1 + 4);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(src.Pixels, (y + y0) * src.Stride + x0 * 4, px, y * w * 4, w * 4);
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
    }

    public static BgraImage? Ask()
    {
        var d = new SignatureDialog();
        return d.ShowDialog() == true ? d.Result : null;
    }
}

/// <summary>Kayıtlı imzaları listeler ve siler.</summary>
public sealed class SignatureManagerDialog : DialogBase
{
    private readonly WrapPanel _list = new();

    private SignatureManagerDialog() : base("Kayıtlı imzalar", 560)
    {
        var st = new StackPanel();
        st.Children.Add(Paragraph("İmzalarınız yalnızca bu bilgisayarda saklanır. Silmek istediğiniz imzanın altındaki düğmeye tıklayın."));
        st.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 380, Margin = new Thickness(0, 12, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var add = new Button { Content = "Yeni imza oluştur…", Margin = new Thickness(0, 12, 0, 0) };
        add.Click += (_, _) => { var r = SignatureDialog.Ask(); if (r is not null) Fill(); };
        st.Children.Add(add);
        Body = st;
        AddButton("Kapat", () => DialogResult = true, primary: true, isCancel: true, isDefault: true);
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var files = SignatureDialog.Saved();
        if (files.Count == 0) { _list.Children.Add(Hint("Kayıtlı imza yok.")); return; }
        foreach (var f in files)
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 12, 12), Width = 160 };
            ImageSource? src = null;
            try { src = ImageCodec.ToBitmapSource(SignatureDialog.Load(f)); } catch { }
            var b = new Border { Height = 70, Background = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Child = new Image { Source = src, Stretch = Stretch.Uniform } };
            b.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
            card.Children.Add(b);
            card.Children.Add(Hint(File.GetLastWriteTime(f).ToString("dd.MM.yyyy HH:mm")));
            var del = new Button { Content = "Sil", Margin = new Thickness(0, 4, 0, 0) };
            var path = f;
            del.Click += (_, _) =>
            {
                try { File.Delete(path); } catch (Exception ex) { MessageDialog.Show("Silinemedi", ex.Message, MessageKind.Error); }
                Fill();
            };
            card.Children.Add(del);
            _list.Children.Add(card);
        }
    }

    public static void ShowDialogFor() => new SignatureManagerDialog().ShowDialog();
}

/// <summary>Not (açıklama) metni sorar.</summary>
public static class NoteDialog
{
    public static string? Ask(string initial = "") => InputDialog.Ask("Açıklama ekle", "Açıklama metni", initial,
        "Not, sayfada simgeyle işaretlenir; Mesly'de yanında balon olarak, diğer PDF okuyucularında simgeye tıklanınca görünür.",
        v => string.IsNullOrWhiteSpace(v) ? "Açıklama boş olamaz." : null);
}

/// <summary>Belgedeki açıklamaları listeler: sayfaya git, sil.</summary>
public sealed class AnnotationListDialog : DialogBase
{
    public AnnotationInfo? GoTo { get; private set; }
    public List<AnnotationInfo> ToDelete { get; } = new();

    private AnnotationListDialog(IReadOnlyList<AnnotationInfo> items) : base("Açıklamalar", 620)
    {
        var st = new StackPanel();
        if (items.Count == 0)
        {
            st.Children.Add(Paragraph("Bu belgede açıklama yok. Düzenleme kipinde Vurgula, Altını Çiz, Üstünü Çiz veya Açıklama araçlarıyla ekleyebilirsiniz."));
            Body = st;
            AddButton("Kapat", () => DialogResult = false, primary: true, isCancel: true, isDefault: true);
            return;
        }
        st.Children.Add(Hint($"{items.Count} açıklama. Satıra çift tıklayarak sayfasına gidin; silmek için işaretleyip “Seçilenleri sil”e basın."));
        var list = new ListBox { MaxHeight = 400, Margin = new Thickness(0, 10, 0, 0), SelectionMode = SelectionMode.Extended };
        foreach (var a in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = a };
            row.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 10, 0), Background = new SolidColorBrush(Color.FromRgb((byte)(a.Rgb >> 16), (byte)(a.Rgb >> 8), (byte)a.Rgb)) });
            row.Children.Add(new TextBlock { Text = $"Sayfa {a.Page + 1}", Width = 70 });
            row.Children.Add(new TextBlock { Text = a.KindName, Width = 100, FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = a.Contents.Replace('\n', ' '), MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Items.Add(new ListBoxItem { Content = row, Tag = a });
        }
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: AnnotationInfo a }) { GoTo = a; DialogResult = true; } };
        st.Children.Add(list);
        Body = st;
        var del = AddButton("Seçilenleri sil", () =>
        {
            ToDelete.AddRange(list.SelectedItems.OfType<ListBoxItem>().Select(i => (AnnotationInfo)i.Tag));
            if (ToDelete.Count > 0) DialogResult = true;
        });
        AddButton("Sayfasına git", () => { if (list.SelectedItem is ListBoxItem { Tag: AnnotationInfo a }) { GoTo = a; DialogResult = true; } });
        AddButton("Kapat", () => DialogResult = false, primary: true, isCancel: true);
    }

    public static AnnotationListDialog? Ask(IReadOnlyList<AnnotationInfo> items)
    {
        var d = new AnnotationListDialog(items);
        return d.ShowDialog() == true ? d : null;
    }
}

/// <summary>Formdaki alanları listeler ve doldurtur.</summary>
public sealed class FormFillDialog : DialogBase
{
    private readonly List<(FormFieldInfo Field, Func<FormFieldChange?> Read)> _rows = new();

    private FormFillDialog(IReadOnlyList<FormFieldInfo> fields) : base("Formu doldur", 600)
    {
        var st = new StackPanel();
        st.Children.Add(Hint("Alanların değerleri belgeye yazılır ve her PDF okuyucusunda aynı görünür. Kaydetmeyi unutmayın."));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        // Radyo düğmeleri aynı adla gruplanır
        foreach (var group in fields.Where(f => f.Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature or FormFieldKind.Unknown)).GroupBy(f => (f.Name, f.Kind == FormFieldKind.Radio ? 0 : f.AnnotIndex * 1000 + f.Page)))
        {
            var f = group.First();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = (string.IsNullOrWhiteSpace(f.Name) ? "(adsız alan)" : f.Name) + $"  · s.{f.Page + 1}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            Grid.SetRow(label, row); grid.Children.Add(label);
            FrameworkElement editor;
            switch (f.Kind)
            {
                case FormFieldKind.Text:
                    var tb = new TextBox { Text = f.Value, IsEnabled = !f.ReadOnly, ClearButtonEnabled = false };
                    editor = tb;
                    _rows.Add((f, () => tb.Text != f.Value ? new FormFieldChange(f, Text: tb.Text) : null));
                    break;
                case FormFieldKind.CheckBox:
                    var cb = new CheckBox { IsChecked = f.Checked, IsEnabled = !f.ReadOnly, Content = string.IsNullOrEmpty(f.ExportValue) ? "" : f.ExportValue };
                    editor = cb;
                    _rows.Add((f, () => (cb.IsChecked == true) != f.Checked ? new FormFieldChange(f, Checked: cb.IsChecked == true) : null));
                    break;
                case FormFieldKind.Radio:
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };
                    foreach (var opt in group)
                    {
                        var rb = new RadioButton { Content = string.IsNullOrEmpty(opt.ExportValue) ? "Seçenek" : opt.ExportValue, GroupName = "rf_" + f.Name, IsChecked = opt.Checked, IsEnabled = !opt.ReadOnly, Margin = new Thickness(0, 0, 14, 0) };
                        var o = opt;
                        panel.Children.Add(rb);
                        _rows.Add((o, () => rb.IsChecked == true && !o.Checked ? new FormFieldChange(o, Checked: true) : null));
                    }
                    editor = panel;
                    break;
                default:
                    var combo = Form.Combo(f.Options.Count > 0 ? f.Options : new[] { "(seçenek yok)" }, Math.Max(0, f.SelectedOption));
                    combo.IsEnabled = !f.ReadOnly && f.Options.Count > 0;
                    editor = combo;
                    _rows.Add((f, () => f.Options.Count > 0 && combo.SelectedIndex != f.SelectedOption ? new FormFieldChange(f, Option: combo.SelectedIndex) : null));
                    break;
            }
            editor.Margin = new Thickness(0, 4, 0, 4);
            editor.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(editor, row); Grid.SetColumn(editor, 1); grid.Children.Add(editor);
            row++;
        }
        st.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body = st;
        AddOkCancel("Uygula");
    }

    public static IReadOnlyList<FormFieldChange>? Ask(IReadOnlyList<FormFieldInfo> fields)
    {
        var d = new FormFillDialog(fields);
        if (d.ShowDialog() != true) return null;
        return d._rows.Select(r => r.Read()).OfType<FormFieldChange>().ToList();
    }
}
