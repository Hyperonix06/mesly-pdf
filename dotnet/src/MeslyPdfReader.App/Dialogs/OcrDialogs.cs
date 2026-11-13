using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Image = System.Windows.Controls.Image;
using PageRange = MeslyPdfReader.Core.PageRange;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>Metin tanıma / görüntü düzeltme seçenekleri.</summary>
public sealed class OcrDialog : DialogBase
{
    private static readonly string[] LangCodes = { "tur+eng", "tur", "eng" };

    public static (IReadOnlyList<int> Pages, OcrRunOptions Options)? Ask(bool ocr, int pageCount, string range, AppSettings s)
    {
        var d = new OcrDialog(ocr ? "Metni tanı (OCR)" : "Taranmış görüntüyü düzelt");
        var box = Form.Text(range);
        var lang = Form.Combo(new[] { "Türkçe + İngilizce", "Türkçe", "İngilizce" }, Math.Max(0, Array.IndexOf(LangCodes, s.OcrLanguages)));
        var dpi = Form.Combo(new[] { "200 DPI (hızlı)", "300 DPI (önerilen)", "400 DPI (küçük yazılar)" }, s.OcrDpi switch { <= 200 => 0, >= 400 => 2, _ => 1 });
        CheckBox Cb(string text, bool v, string tip) => new() { Content = text, IsChecked = v, Margin = new Thickness(0, 6, 0, 0), ToolTip = tip };
        var rot = Cb("Sayfa yönünü otomatik düzelt", s.OcrAutoRotate, "Yan veya ters sayfaları metne bakarak dik çevirir (kayıpsız sayfa döndürme).");
        var skew = Cb("Eğikliği düzelt", s.OcrDeskew, "Eğri taranmış satırları yataya getirir (taranmış görüntü yeniden yazılır).");
        var borders = Cb("Kenar gölgelerini temizle", s.OcrCleanBorders, "Tarayıcı kapağından kalan koyu kenar şeritlerini beyazlatır.");
        var whiten = Cb("Arka planı beyazlat", s.OcrWhiten, "Sararmış, gri veya gölgeli kâğıdı beyaza çeker; ışık farkını giderir.");
        var skip = Cb("Zaten metin içeren sayfaları atla", true, "Seçilebilir metni olan sayfalar yeniden tanınmaz.");
        var err = Error();
        var st = new StackPanel();
        st.Children.Add(Paragraph(ocr
            ? "Sayfalardaki görüntü metne çevrilir ve görünmez bir metin katmanı olarak eklenir: sayfa aynı görünür, ancak aranabilir, kopyalanabilir ve Düzenle aracıyla düzenlenebilir olur. Tanıma tamamen bu bilgisayarda yapılır; belge hiçbir yere gönderilmez."
            : "Taranmış sayfaların yönü, eğikliği ve kenarları düzeltilir. Metin tanınmaz."));
        st.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        if (ocr) st.Children.Add(Form.TwoColumns(Form.Labeled("Belge dili", lang), Form.Labeled("Tanıma çözünürlüğü", dpi)));
        st.Children.Add(Label("Görüntü düzeltme", 14));
        st.Children.Add(rot); st.Children.Add(skew); st.Children.Add(borders); st.Children.Add(whiten);
        if (ocr) st.Children.Add(skip);
        st.Children.Add(err);
        d.Body = st;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel(ocr ? "Tanı" : "Düzelt", () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            if (ok && !ocr && rot.IsChecked != true && skew.IsChecked != true && borders.IsChecked != true && whiten.IsChecked != true) { ok = false; e = "En az bir düzeltme seçin."; }
            ShowError(err, e);
            return ok;
        });
        if (d.ShowDialog() != true) return null;
        s.OcrLanguages = LangCodes[Math.Max(0, lang.SelectedIndex)];
        s.OcrDpi = dpi.SelectedIndex switch { 0 => 200, 2 => 400, _ => 300 };
        s.OcrAutoRotate = rot.IsChecked == true; s.OcrDeskew = skew.IsChecked == true; s.OcrCleanBorders = borders.IsChecked == true; s.OcrWhiten = whiten.IsChecked == true;
        return (pages, OcrRunOptions.From(s) with { SkipPagesWithText = !ocr || skip.IsChecked == true });
    }

    private OcrDialog(string t) : base(t, 560) { }
}

/// <summary>
/// Tanıma doğrulama: güveni düşük sözcükler sayfadaki görüntüsüyle birlikte gösterilir; kullanıcı onaylar veya düzeltir.
/// Düzeltilen sözcükler metin katmanına yazılır.
/// </summary>
public sealed class OcrVerifyDialog : DialogBase
{
    private sealed record Entry(int Page, int WordIndex, OcrWord Word)
    {
        public string Corrected { get; set; } = Word.Text;
        public bool Done { get; set; }
        public override string ToString() => $"{(Done ? "✓ " : "")}Sayfa {Page + 1} · {Corrected}   (%{Word.Confidence:0})";
    }

    public static Task<Dictionary<int, OcrPage>> ShowAsync(PdfDocument doc, IReadOnlyDictionary<int, OcrPage> results, int dpi, float limit = 60)
    {
        var entries = results.OrderBy(r => r.Key)
            .SelectMany(r => r.Value.Words.Select((w, i) => new Entry(r.Key, i, w)))
            .Where(e => e.Word.Confidence < limit).ToList();
        var changed = new Dictionary<int, OcrPage>();
        if (entries.Count == 0)
        {
            MessageDialog.Show("Doğrulama", "Tüm sözcükler yüksek güvenle tanındı; kontrol edilecek sözcük yok.", MessageKind.Info);
            return Task.FromResult(changed);
        }
        var d = new OcrVerifyDialog(entries.Count);
        var list = new ListBox { Height = 360, MinWidth = 250 };
        foreach (var e in entries) list.Items.Add(e);
        var img = new Image { Height = 120, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 10) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var imgBorder = new Border { Child = img, BorderThickness = new Thickness(1), Padding = new Thickness(6), Background = Brushes.White };
        imgBorder.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var box = Form.Text("");
        box.FontSize = 18;
        var info = Hint("");
        var fix = new Wpf.Ui.Controls.Button { Content = "Düzelt ve sonraki", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 10, 8, 0), ToolTip = "Enter" };
        var accept = new Wpf.Ui.Controls.Button { Content = "Doğru, sonraki", Margin = new Thickness(0, 10, 0, 0), ToolTip = "Ctrl+Enter" };
        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(Label("Sayfadaki görüntü", 0));
        right.Children.Add(imgBorder);
        right.Children.Add(Label("Tanınan metin (düzeltebilirsiniz)", 4));
        right.Children.Add(box);
        right.Children.Add(info);
        right.Children.Add(Form.Row(fix, accept));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(list);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var st = new StackPanel();
        st.Children.Add(Paragraph($"{entries.Count} sözcük düşük güvenle tanındı. Her birini sayfadaki görüntüsüyle karşılaştırın; yanlışsa düzeltin."));
        st.Children.Add(new Border { Height = 10 });
        st.Children.Add(grid);
        d.Body = st;

        int renderSeq = 0;
        async void ShowEntry()
        {
            if (list.SelectedItem is not Entry e) return;
            box.Text = e.Corrected;
            box.Focus(); box.SelectAll();
            info.Text = $"Güven: %{e.Word.Confidence:0} · sayfa {e.Page + 1}";
            int seq = ++renderSeq;
            try
            {
                var ocr = results[e.Page];
                var w = e.Word;
                int padX = Math.Max(w.Height * 3, 30), padY = Math.Max(w.Height / 2, 8);
                int x = Math.Max(0, w.Left - padX), y = Math.Max(0, w.Top - padY);
                int rw = Math.Min(ocr.Width - x, w.Width + 2 * padX), rh = Math.Min(ocr.Height - y, w.Height + 2 * padY);
                var bmp = await doc.RenderAsync(e.Page, new RenderRequest(ocr.Width, ocr.Height, x, y, rw, rh));
                // Sözcüğün çevresine çerçeve çiz.
                int bx0 = w.Left - x - 2, by0 = w.Top - y - 2, bx1 = w.Right - x + 2, by1 = w.Bottom - y + 2;
                for (int t = 0; t < 2; t++)
                {
                    ImageOps.FillRect(bmp, bx0 - t, by0 - t, bx1 + t, by0 - t + 1, 0xF0, 0x7B, 0x3D);
                    ImageOps.FillRect(bmp, bx0 - t, by1 + t - 1, bx1 + t, by1 + t, 0xF0, 0x7B, 0x3D);
                    ImageOps.FillRect(bmp, bx0 - t, by0 - t, bx0 - t + 1, by1 + t, 0xF0, 0x7B, 0x3D);
                    ImageOps.FillRect(bmp, bx1 + t - 1, by0 - t, bx1 + t, by1 + t, 0xF0, 0x7B, 0x3D);
                }
                if (seq == renderSeq) img.Source = ImageCodec.ToBitmapSource(bmp);
            }
            catch (Exception ex) { info.Text = "Görüntü gösterilemedi: " + ex.Message; }
        }
        void Next(bool keep)
        {
            if (list.SelectedItem is not Entry e) return;
            var t = box.Text.Trim();
            if (!keep && t.Length > 0) e.Corrected = t;
            e.Done = true;
            int i = list.SelectedIndex;
            list.Items.Refresh();
            if (i + 1 < list.Items.Count) list.SelectedIndex = i + 1; else list.SelectedIndex = i;
        }
        list.SelectionChanged += (_, _) => ShowEntry();
        fix.Click += (_, _) => Next(false);
        accept.Click += (_, _) => Next(true);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Next(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        };
        d.AddOkCancel("Düzeltmeleri uygula");
        d.Loaded += (_, _) => list.SelectedIndex = 0;
        if (d.ShowDialog() != true) return Task.FromResult(changed);
        foreach (var g in entries.Where(e => e.Corrected != e.Word.Text).GroupBy(e => e.Page))
        {
            var page = results[g.Key];
            var words = page.Words.ToList();
            foreach (var e in g) words[e.WordIndex] = e.Word with { Text = e.Corrected, Confidence = 100 };
            changed[g.Key] = page with { Words = words };
        }
        return Task.FromResult(changed);
    }

    private OcrVerifyDialog(int n) : base("Tanımayı doğrula", 820) { }
}
