using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using PageRange = MeslyPdfReader.Core.PageRange;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>Hazır renk listesi + onaltılık (#RRGGBB) alan.</summary>
internal sealed class ColorField
{
    public static readonly (string Name, uint Rgb)[] Palette =
    {
        ("Siyah", 0x000000), ("Koyu gri", 0x444444), ("Gri", 0x888888), ("Açık gri", 0xCCCCCC), ("Beyaz", 0xFFFFFF),
        ("Kırmızı", 0xD32F2F), ("Turuncu", 0xF57C00), ("Sarı", 0xFBC02D), ("Yeşil", 0x388E3C), ("Turkuaz", 0x0097A7),
        ("Mavi", 0x1E5EF0), ("Lacivert", 0x1A237E), ("Mor", 0x7B1FA2), ("Kahverengi", 0x5D4037),
        ("Açık sarı", 0xFFF8D6), ("Açık mavi", 0xE6F0FF), ("Açık yeşil", 0xE8F5E9),
    };

    public ComboBox Combo { get; } = new() { MinWidth = 150 };
    public TextBox HexBox { get; } = Form.Text("#000000", 100);
    public StackPanel Panel { get; }

    public ColorField(uint initial)
    {
        foreach (var (name, rgb) in Palette)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Rectangle { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), Stroke = Brushes.Gray, StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) });
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Combo.Items.Add(new ComboBoxItem { Content = sp, Tag = rgb });
        }
        HexBox.Margin = new Thickness(8, 0, 0, 0);
        Combo.SelectionChanged += (_, _) => { if (Combo.SelectedItem is ComboBoxItem { Tag: uint v }) HexBox.Text = DocumentViewModel.Hex(v); };
        Panel = Form.Row(Combo, HexBox);
        Set(initial);
    }

    public void Set(uint rgb)
    {
        int i = Array.FindIndex(Palette, p => p.Rgb == rgb);
        if (i >= 0) Combo.SelectedIndex = i; else Combo.SelectedIndex = -1;
        HexBox.Text = DocumentViewModel.Hex(rgb);
    }

    public uint? Value => DocumentViewModel.ParseRgb(HexBox.Text);
}

internal static class FontCombo
{
    /// <summary>Kurulu yazı tipi aileleri (dizin hazır değilse temel liste).</summary>
    public static ComboBox Create(string selected = "Arial")
    {
        var c = new ComboBox { IsEditable = true, IsTextSearchEnabled = true };
        var task = FontResolver.Instance.GetFamiliesAsync();
        var list = task.IsCompletedSuccessfully ? task.Result : new[] { "Arial", "Calibri", "Segoe UI", "Times New Roman", "DejaVu Sans" };
        foreach (var f in list) c.Items.Add(f);
        c.Text = selected;
        return c;
    }
}

public sealed class LinkDialog : DialogBase
{
    public static string? Ask(string initial = "https://")
    {
        var d = new LinkDialog();
        var box = Form.Text(initial);
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Paragraph("Çizdiğiniz alana tıklanabilir bir web bağlantısı eklenir."));
        s.Children.Add(Form.Labeled("Adres", box));
        s.Children.Add(Hint("http://, https:// veya mailto: ile başlamalı. Belge içi sayfa bağlantıları bu sürümde eklenemez."));
        s.Children.Add(err);
        d.Body = s;
        d.AddOkCancel("Ekle", () =>
        {
            var ok = Uri.TryCreate(box.Text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto";
            ShowError(err, ok ? null : "Geçerli bir adres girin (ör. https://ornek.com).");
            return ok;
        });
        return d.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private LinkDialog() : base("Bağlantı ekle", 460) { }
}

public sealed class WatermarkDialog : DialogBase
{
    public static (string Text, string Family, double Size, uint Rgb, double Angle, double Opacity, IReadOnlyList<int> Pages)? Ask(int pageCount, int current)
    {
        var d = new WatermarkDialog();
        var text = Form.Text("GİZLİ");
        var font = FontCombo.Create();
        var size = Form.Number(0, 0, 400);
        var color = new ColorField(0xD32F2F);
        var angle = Form.Number(45, -90, 90);
        var opacity = Form.Number(20, 3, 100);
        var range = Form.Text("tümü");
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Form.Labeled("Filigran metni", text, 0));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Yazı tipi", font), Form.Labeled("Boyut (pt, 0 = sayfaya göre)", size)));
        s.Children.Add(Form.Labeled("Renk", color.Panel));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Açı (°)", angle), Form.Labeled("Opaklık (%)", opacity)));
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", range));
        s.Children.Add(Hint("Filigran gerçek, aranabilir metin nesnesi olarak sayfa içeriğine yazılır ve “Filigranları kaldır” ile silinebilir."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Ekle", () =>
        {
            string? e = string.IsNullOrWhiteSpace(text.Text) ? "Filigran metni boş olamaz." : color.Value is null ? "Renk #RRGGBB biçiminde olmalı." : null;
            if (e is null && !PageRange.TryParse(range.Text, pageCount, out pages, out var pe)) e = pe;
            ShowError(err, e);
            return e is null;
        });
        if (d.ShowDialog() != true) return null;
        return (text.Text.Trim(), font.Text, Form.Val(size), color.Value!.Value, Form.Val(angle), Form.Val(opacity) / 100.0, pages);
    }

    private WatermarkDialog() : base("Filigran ekle", 520) { }
}

public sealed class HeaderFooterDialog : DialogBase
{
    public static (IReadOnlyList<HeaderFooterItem> Items, string Family, double Size, uint Rgb, double Margin, int Start, IReadOnlyList<int> Pages)? Ask(int pageCount, bool pageNumbersOnly)
    {
        var d = new HeaderFooterDialog(pageNumbersOnly ? "Sayfa numarası ekle" : "Üstbilgi / altbilgi ekle");
        var boxes = Enum.GetValues<HFPosition>().ToDictionary(p => p, _ => Form.Text(""));
        if (pageNumbersOnly) boxes[HFPosition.BottomCenter].Text = "{sayfa} / {toplam}";
        else { boxes[HFPosition.TopLeft].Text = "{dosya}"; boxes[HFPosition.BottomCenter].Text = "Sayfa {sayfa} / {toplam}"; boxes[HFPosition.BottomRight].Text = "{tarih}"; }
        var font = FontCombo.Create();
        var size = Form.Number(9, 4, 72, 1);
        var color = new ColorField(0x444444);
        var margin = Form.Number(10, 2, 60, 1);
        var start = Form.Number(1, 0, 100000);
        var range = Form.Text("tümü");
        var err = Error();
        var s = new StackPanel();
        Grid Three(HFPosition a, HFPosition b, HFPosition c)
        {
            var g = new Grid();
            for (int i = 0; i < 5; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(8) : new GridLength(1, GridUnitType.Star) });
            var names = new[] { "Sol", "Orta", "Sağ" };
            var pos = new[] { a, b, c };
            for (int i = 0; i < 3; i++) { var l = Form.Labeled(names[i], boxes[pos[i]], 4); Grid.SetColumn(l, i * 2); g.Children.Add(l); }
            return g;
        }
        s.Children.Add(Label("Üstbilgi", 0));
        s.Children.Add(Three(HFPosition.TopLeft, HFPosition.TopCenter, HFPosition.TopRight));
        s.Children.Add(Label("Altbilgi", 12));
        s.Children.Add(Three(HFPosition.BottomLeft, HFPosition.BottomCenter, HFPosition.BottomRight));
        s.Children.Add(Hint("Yer tutucular: {sayfa}, {toplam}, {tarih}, {dosya}. Boş bırakılan konumlara bir şey yazılmaz."));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Yazı tipi", font), Form.Labeled("Boyut (pt)", size)));
        s.Children.Add(Form.Labeled("Renk", color.Panel));
        s.Children.Add(Form.TwoColumns(Form.Labeled("Kenar boşluğu (mm)", margin), Form.Labeled("İlk sayfa numarası", start)));
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", range));
        s.Children.Add(Hint("Numaralandırma seçilen ilk sayfadan başlar; {toplam} seçilen sayfa sayısına göre hesaplanır. Döndürülmüş sayfalarda metin okunma yönüne göre yerleşir."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Ekle", () =>
        {
            string? e = boxes.Values.All(b => string.IsNullOrWhiteSpace(b.Text)) ? "En az bir konuma metin yazın." : color.Value is null ? "Renk #RRGGBB biçiminde olmalı." : null;
            if (e is null && !PageRange.TryParse(range.Text, pageCount, out pages, out var pe)) e = pe;
            ShowError(err, e);
            return e is null;
        });
        if (d.ShowDialog() != true) return null;
        var items = boxes.Where(b => !string.IsNullOrWhiteSpace(b.Value.Text)).Select(b => new HeaderFooterItem(b.Key, b.Value.Text.Trim())).ToList();
        return (items, font.Text, Form.Val(size), color.Value!.Value, PaperSize.MmToPt(Form.Val(margin)), (int)Form.Val(start), pages);
    }

    private HeaderFooterDialog(string t) : base(t, 600) { }
}

public sealed class BackgroundDialog : DialogBase
{
    public static (uint Rgb, IReadOnlyList<int> Pages)? Ask(int pageCount, int current)
    {
        var d = new BackgroundDialog();
        var color = new ColorField(0xFFF8D6);
        var range = Form.Text("tümü");
        var err = Error();
        var s = new StackPanel();
        s.Children.Add(Form.Labeled("Arka plan rengi", color.Panel, 0));
        s.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", range));
        s.Children.Add(Hint("Renk, sayfa içeriğinin en altına (tüm metin ve görsellerin arkasına) bir dolgu olarak eklenir. Daha önce eklenen arka planın yerine geçer."));
        s.Children.Add(err);
        d.Body = s;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Uygula", () =>
        {
            string? e = color.Value is null ? "Renk #RRGGBB biçiminde olmalı." : null;
            if (e is null && !PageRange.TryParse(range.Text, pageCount, out pages, out var pe)) e = pe;
            ShowError(err, e);
            return e is null;
        });
        return d.ShowDialog() == true ? (color.Value!.Value, pages) : null;
    }

    private BackgroundDialog() : base("Sayfa arka planı", 480) { }
}
