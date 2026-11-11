using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using PageRange = MeslyPdfReader.Core.PageRange;
using TextSearch = MeslyPdfReader.Core.TextSearch;

namespace MeslyPdfReader.App.Dialogs;

internal static class Form
{
    public static TextBox Text(string value, double width = double.NaN) => new() { Text = value, Width = width, ClearButtonEnabled = false };

    public static NumberBox Number(double value, double min, double max, int decimals = 0, double step = 1) => new()
    {
        Value = value, Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10,
        MaxDecimalPlaces = decimals, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, ClearButtonEnabled = false,
    };

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0)
    {
        var c = new ComboBox();
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, c.Items.Count - 1));
        return c;
    }

    public static RadioButton Radio(string text, string group, bool isChecked = false) => new() { Content = text, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 4, 16, 4) };

    public static Grid TwoColumns(UIElement a, UIElement b, double gap = 12)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(a);
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
        return g;
    }

    public static StackPanel Labeled(string label, UIElement control, double top = 10)
    {
        var s = new StackPanel();
        s.Children.Add(DialogBase.Label(label, top));
        s.Children.Add(control);
        return s;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    public static double Val(NumberBox n) => n.Value ?? 0;

    /// <summary>Kâğıt seçici: standart boyutlar + özel (mm).</summary>
    public sealed class PaperPicker
    {
        public ComboBox Combo { get; }
        public NumberBox W { get; } = Number(210, 10, 5000, 1);
        public NumberBox H { get; } = Number(297, 10, 5000, 1);
        public ComboBox Orientation { get; } = Form.Combo(new[] { "Dikey", "Yatay" });
        public StackPanel Panel { get; } = new();

        public PaperPicker(SizeF current, bool includeCurrent)
        {
            var names = PaperSize.Standard.Select(p => p.ToString()).ToList();
            if (includeCurrent) names.Insert(0, $"Geçerli sayfa ({PaperSize.Describe(current)})");
            names.Add("Özel boyut");
            Combo = Form.Combo(names, includeCurrent ? 0 : 0);
            W.Value = Math.Round(PaperSize.PtToMm(Math.Min(current.Width, current.Height)), 1);
            H.Value = Math.Round(PaperSize.PtToMm(Math.Max(current.Width, current.Height)), 1);
            Orientation.SelectedIndex = current.IsLandscape ? 1 : 0;
            var custom = TwoColumns(Labeled("Genişlik (mm)", W), Labeled("Yükseklik (mm)", H));
            custom.Visibility = Visibility.Collapsed;
            Combo.SelectionChanged += (_, _) => custom.Visibility = Combo.SelectedIndex == Combo.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Children.Add(TwoColumns(Labeled("Kâğıt boyutu", Combo, 0), Labeled("Yön", Orientation, 0)));
            Panel.Children.Add(custom);
            _includeCurrent = includeCurrent; _current = current;
        }

        private readonly bool _includeCurrent;
        private readonly SizeF _current;

        public SizeF Value
        {
            get
            {
                int i = Combo.SelectedIndex;
                SizeF s;
                if (_includeCurrent && i == 0) s = _current;
                else if (i == Combo.Items.Count - 1) s = new SizeF(PaperSize.MmToPt(Val(W)), PaperSize.MmToPt(Val(H)));
                else s = PaperSize.Standard[i - (_includeCurrent ? 1 : 0)].ToPoints();
                bool landscape = Orientation.SelectedIndex == 1;
                return landscape == s.IsLandscape ? s : s.Swap();
            }
        }
    }
}

public sealed class InsertBlankDialog : DialogBase
{
    public static (int At, int Count, SizeF Size)? Ask(int pageCount, int current, SizeF currentSize, bool forNewDocument = false)
    {
        var d = new InsertBlankDialog(forNewDocument ? "Yeni boş PDF" : "Boş sayfa ekle");
        var count = Form.Number(1, 1, 5000);
        var paper = new Form.PaperPicker(forNewDocument ? PaperSize.A4.ToPoints() : currentSize, includeCurrent: !forNewDocument);
        var pos = Form.Combo(new[] { $"Geçerli sayfadan ({current + 1}) sonra", $"Geçerli sayfadan ({current + 1}) önce", "Belgenin başına", "Belgenin sonuna" });
        var s = new StackPanel();
        s.Children.Add(paper.Panel);
        s.Children.Add(Form.Labeled("Sayfa sayısı", count));
        if (!forNewDocument) s.Children.Add(Form.Labeled("Konum", pos));
        d.Body = s;
        d.AddOkCancel(forNewDocument ? "Oluştur" : "Ekle");
        if (d.ShowDialog() != true) return null;
        int at = pos.SelectedIndex switch { 0 => current + 1, 1 => current, 2 => 0, _ => pageCount };
        return (forNewDocument ? 0 : at, (int)Form.Val(count), paper.Value);
    }

    private InsertBlankDialog(string t) : base(t, 480) { }
}

public sealed class InsertPositionDialog : DialogBase
{
    public static int? Ask(int pageCount, int current, string what)
    {
        var d = new InsertPositionDialog();
        var pos = Form.Combo(new[] { $"Geçerli sayfadan ({current + 1}) sonra", $"Geçerli sayfadan ({current + 1}) önce", "Belgenin başına", "Belgenin sonuna", "Şu sayfadan sonra…" });
        var num = Form.Number(current + 1, 0, pageCount);
        num.Visibility = Visibility.Collapsed;
        num.Margin = new Thickness(0, 8, 0, 0);
        pos.SelectionChanged += (_, _) => num.Visibility = pos.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        var s = new StackPanel();
        s.Children.Add(Paragraph($"Eklenecek: {what}"));
        s.Children.Add(Form.Labeled("Konum", pos));
        s.Children.Add(num);
        s.Children.Add(Hint("“Şu sayfadan sonra” için 0 yazarsanız sayfalar en başa eklenir."));
        d.Body = s;
        d.AddOkCancel("Ekle");
        if (d.ShowDialog() != true) return null;
        return pos.SelectedIndex switch { 0 => current + 1, 1 => current, 2 => 0, 3 => pageCount, _ => (int)Form.Val(num) };
    }

    private InsertPositionDialog() : base("Sayfa ekle", 460) { }
}

public sealed class PageRangeDialog : DialogBase
{
    public static (IReadOnlyList<int> Pages, bool Separate)? Ask(string title, string description, string range, int pageCount, string okText, bool allowSeparateFiles = false)
    {
        var d = new PageRangeDialog(title);
        var box = Form.Text(range);
        var sep = new CheckBox { Content = "Her sayfayı ayrı bir dosya olarak kaydet", Margin = new Thickness(0, 12, 0, 0) };
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Paragraph(description));
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        s.Children.Add(Hint("Örnek: 1-3, 5, 8-son · “tek”, “çift” veya “tümü” de yazabilirsiniz."));
        if (allowSeparateFiles) s.Children.Add(sep);
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel(okText, () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            ShowError(err, e);
            return ok;
        });
        return d.ShowDialog() == true ? (pages, sep.IsChecked == true) : null;
    }

    private PageRangeDialog(string t) : base(t, 460) { }
}

public sealed class CropDialog : DialogBase
{
    public static (IReadOnlyList<int> Pages, Margins Margins)? Ask(int pageCount, string range, SizeF current)
    {
        var d = new CropDialog();
        var l = Form.Number(10, 0, 1000, 1); var t = Form.Number(10, 0, 1000, 1);
        var r = Form.Number(10, 0, 1000, 1); var b = Form.Number(10, 0, 1000, 1);
        var box = Form.Text(range);
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Paragraph($"Sayfaların kenarlarından kırpılacak payı milimetre olarak girin. Geçerli sayfa: {PaperSize.PtToMm(current.Width):0} × {PaperSize.PtToMm(current.Height):0} mm."));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Üst (mm)", t), Form.Labeled("Alt (mm)", b)));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Sol (mm)", l), Form.Labeled("Sağ (mm)", r)));
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        s.Children.Add(Hint("Kırpma, sayfanın görünür alanını (CropBox) değiştirir; içerik silinmez ve geri alınabilir."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Kırp", () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            if (ok && Form.Val(l) + Form.Val(r) >= PaperSize.PtToMm(current.Width) - 5) { ok = false; e = "Sol ve sağ pay toplamı sayfa genişliğinden küçük olmalı."; }
            if (ok && Form.Val(t) + Form.Val(b) >= PaperSize.PtToMm(current.Height) - 5) { ok = false; e = "Üst ve alt pay toplamı sayfa yüksekliğinden küçük olmalı."; }
            ShowError(err, e);
            return ok;
        });
        return d.ShowDialog() == true ? (pages, Margins.FromMm(Form.Val(l), Form.Val(t), Form.Val(r), Form.Val(b))) : null;
    }

    private CropDialog() : base("Sayfaları kırp", 480) { }
}

public sealed class PageSizeDialog : DialogBase
{
    public static (IReadOnlyList<int> Pages, SizeF Size, bool KeepOrientation)? Ask(int pageCount, string range, SizeF current)
    {
        var d = new PageSizeDialog();
        var paper = new Form.PaperPicker(PaperSize.A4.ToPoints(), includeCurrent: false);
        var keep = new CheckBox { Content = "Her sayfanın kendi yönünü (dikey/yatay) koru", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        var box = Form.Text(range);
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(paper.Panel);
        s.Children.Add(keep);
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        s.Children.Add(Hint("İçerik orantısı korunarak yeni boyuta sığdırılır ve ortalanır."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Uygula", () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            ShowError(err, e);
            return ok;
        });
        return d.ShowDialog() == true ? (pages, paper.Value, keep.IsChecked == true) : null;
    }

    private PageSizeDialog() : base("Sayfa boyutunu değiştir", 500) { }
}

public sealed record SplitRequest(SplitPlan Plan, string Folder, string BaseName);

public sealed class SplitDialog : DialogBase
{
    public static SplitRequest? Ask(int pageCount, string baseName, string? folder)
    {
        var d = new SplitDialog();
        var rN = Form.Radio("Her N sayfada bir dosya", "mode", true);
        var n = Form.Number(Math.Max(1, Math.Min(10, pageCount / 2)), 1, Math.Max(1, pageCount));
        var rRanges = Form.Radio("Sayfa aralıklarına göre", "mode");
        var ranges = Form.Text($"1-{Math.Max(1, pageCount / 2)}; {Math.Min(pageCount, pageCount / 2 + 1)}-son");
        var rSingle = Form.Radio("Her sayfa ayrı dosya", "mode");
        var rOdd = Form.Radio("Tek ve çift sayfalar (2 dosya)", "mode");
        var folderBox = Form.Text(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        var browse = new Wpf.Ui.Controls.Button { Content = "Gözat…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => { var f = FileDialogs.PickFolder("Bölünen dosyaların klasörü", folderBox.Text); if (f is not null) folderBox.Text = f; };
        var name = Form.Text(baseName);
        var preview = Hint("");
        var err = Error();
        void Update()
        {
            try
            {
                var groups = PdfTools.PlanSplit(pageCount, Plan());
                preview.Text = $"{groups.Count} dosya oluşturulacak: " + string.Join(", ", groups.Take(4).Select(g => PageRange.Format(g))) + (groups.Count > 4 ? ", …" : "");
                ShowError(err, null);
            }
            catch (FormatException ex) { preview.Text = ""; ShowError(err, ex.Message); }
        }
        SplitPlan Plan() => rN.IsChecked == true ? new SplitPlan(SplitMode.EveryNPages, (int)Form.Val(n))
            : rRanges.IsChecked == true ? new SplitPlan(SplitMode.ByRanges, Ranges: ranges.Text)
            : rSingle.IsChecked == true ? new SplitPlan(SplitMode.SinglePages) : new SplitPlan(SplitMode.OddEven);
        foreach (var rb in new[] { rN, rRanges, rSingle, rOdd }) rb.Checked += (_, _) => Update();
        n.ValueChanged += (_, _) => Update();
        ranges.TextChanged += (_, _) => Update();

        var folderGrid = new Grid();
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition());
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderGrid.Children.Add(folderBox); Grid.SetColumn(browse, 1); folderGrid.Children.Add(browse);

        var s = new StackPanel();
        s.Children.Add(Paragraph($"Belge {pageCount} sayfa. Bölme yöntemini seçin:"));
        s.Children.Add(rN); n.Margin = new Thickness(26, 0, 0, 6); s.Children.Add(n);
        s.Children.Add(rRanges); ranges.Margin = new Thickness(26, 0, 0, 2); s.Children.Add(ranges);
        var rh = Hint("Aralıkları noktalı virgülle ayırın. Örnek: 1-3; 4-10; 11-son"); rh.Margin = new Thickness(26, 0, 0, 6); s.Children.Add(rh);
        s.Children.Add(rSingle);
        s.Children.Add(rOdd);
        s.Children.Add(Form.Labeled("Hedef klasör", folderGrid, 14));
        s.Children.Add(Form.Labeled("Dosya adı öneki", name));
        s.Children.Add(preview);
        s.Children.Add(err);
        d.Body = s;
        Update();
        d.AddOkCancel("Böl", () =>
        {
            try { PdfTools.PlanSplit(pageCount, Plan()); }
            catch (FormatException ex) { ShowError(err, ex.Message); return false; }
            if (string.IsNullOrWhiteSpace(folderBox.Text)) { ShowError(err, "Hedef klasör seçin."); return false; }
            if (string.IsNullOrWhiteSpace(name.Text)) { ShowError(err, "Dosya adı öneki boş olamaz."); return false; }
            return true;
        });
        return d.ShowDialog() == true ? new SplitRequest(Plan(), folderBox.Text.Trim(), name.Text.Trim()) : null;
    }

    private SplitDialog() : base("PDF'yi böl", 520) { }
}

public sealed record ExportImagesRequest(IReadOnlyList<int> Pages, ImageFormatKind Format, int Dpi, int Quality, bool SingleTiff, string Folder);

public sealed class ExportImagesDialog : DialogBase
{
    public static ExportImagesRequest? Ask(int pageCount, string range, int dpi, int quality, string? folder)
    {
        var d = new ExportImagesDialog();
        var fmt = Form.Combo(new[] { "PNG (kayıpsız)", "JPG", "TIFF", "BMP" });
        var dpiBox = Form.Number(dpi, 36, 1200);
        var q = Form.Number(quality, 10, 100);
        var single = new CheckBox { Content = "Tüm sayfaları tek bir çok sayfalı TIFF dosyasına yaz", Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var qPanel = Form.Labeled("JPG kalitesi (10–100)", q);
        qPanel.Visibility = Visibility.Collapsed;
        fmt.SelectionChanged += (_, _) =>
        {
            qPanel.Visibility = fmt.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            single.Visibility = fmt.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        var box = Form.Text(range);
        var folderBox = Form.Text(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        var browse = new Wpf.Ui.Controls.Button { Content = "Gözat…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => { var f = FileDialogs.PickFolder("Görüntülerin kaydedileceği klasör", folderBox.Text); if (f is not null) folderBox.Text = f; };
        var fg = new Grid();
        fg.ColumnDefinitions.Add(new ColumnDefinition()); fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fg.Children.Add(folderBox); Grid.SetColumn(browse, 1); fg.Children.Add(browse);
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Form.TwoColumns(Form.Labeled("Biçim", fmt, 0), Form.Labeled("Çözünürlük (DPI)", dpiBox, 0)));
        s.Children.Add(qPanel);
        s.Children.Add(single);
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        s.Children.Add(Form.Labeled("Hedef klasör", fg));
        s.Children.Add(Hint("150 DPI ekranda okuma, 300 DPI baskı ve arşiv için uygundur."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Dışa aktar", () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            if (ok && !Directory.Exists(folderBox.Text)) { try { Directory.CreateDirectory(folderBox.Text); } catch { ok = false; e = "Hedef klasör oluşturulamadı."; } }
            ShowError(err, e);
            return ok;
        });
        if (d.ShowDialog() != true) return null;
        var kind = fmt.SelectedIndex switch { 1 => ImageFormatKind.Jpeg, 2 => ImageFormatKind.Tiff, 3 => ImageFormatKind.Bmp, _ => ImageFormatKind.Png };
        return new ExportImagesRequest(pages, kind, (int)Form.Val(dpiBox), (int)Form.Val(q), single.IsChecked == true && kind == ImageFormatKind.Tiff, folderBox.Text.Trim());
    }

    private ExportImagesDialog() : base("Sayfaları görüntü olarak dışa aktar", 520) { }
}

public sealed class ConvertChoiceDialog : DialogBase
{
    public static string? Ask(string docName)
    {
        var d = new ConvertChoiceDialog();
        string? choice = null;
        var s = new StackPanel();
        s.Children.Add(Paragraph($"“{docName}” ne olarak dışa aktarılsın?"));
        foreach (var (key, title, desc, icon) in new[]
        {
            ("word", "Microsoft Word (.docx)", "Başlıklar, paragraflar, tablolar ve resimler düzenlenebilir Word belgesi olur.", SymbolRegular.DocumentText24),
            ("excel", "Microsoft Excel (.xlsx)", "Tablolar hücrelere, sayılar hesaplanabilir sayılara dönüşür.", SymbolRegular.Table24),
            ("images", "Görüntü (PNG, JPG, TIFF, BMP)", "Her sayfa seçtiğiniz çözünürlükte bir görüntü dosyası olur.", SymbolRegular.ImageMultiple24),
            ("text", "Düz metin (TXT)", "Belgedeki seçilebilir metin UTF-8 metin dosyasına yazılır.", SymbolRegular.TextT24),
        })
        {
            var b = new Wpf.Ui.Controls.Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 0) };
            var g = new StackPanel { Orientation = Orientation.Horizontal };
            g.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 24, Margin = new Thickness(0, 0, 12, 0) });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            t.Children.Add(Hint(desc));
            g.Children.Add(t);
            b.Content = g;
            var k = key;
            b.Click += (_, _) => { choice = k; d.DialogResult = true; };
            s.Children.Add(b);
        }
        s.Children.Add(Hint("Taranmış sayfalar Word/Excel dönüşümünde önce tanınır (OCR). PowerPoint (.pptx) dönüşümü henüz yok."));
        d.Body = s;
        d.AddButton("İptal", () => d.DialogResult = false, isCancel: true);
        return d.ShowDialog() == true ? choice : null;
    }

    private ConvertChoiceDialog() : base("PDF dönüştür", 480) { }
}
