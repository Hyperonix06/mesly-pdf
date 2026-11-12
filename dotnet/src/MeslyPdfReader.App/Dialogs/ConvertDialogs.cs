using System.Windows;
using System.Windows.Controls;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;
using PageRange = MeslyPdfReader.Core.PageRange;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>PDF → Word / Excel dönüştürme seçenekleri.</summary>
public sealed class ConvertDialog : DialogBase
{
    private static readonly string[] LangCodes = { "tur+eng", "tur", "eng" };

    public static ConvertRequest? Ask(ConvertFormat format, int pageCount, string range, int scannedPages, AppSettings s)
    {
        bool word = format is ConvertFormat.Word or ConvertFormat.PowerPoint;
        bool ppt = format == ConvertFormat.PowerPoint;
        var d = new ConvertDialog(ppt ? "Microsoft PowerPoint'e dönüştür" : word ? "Microsoft Word'e dönüştür" : "Microsoft Excel'e dönüştür");
        var box = Form.Text(range);
        CheckBox Cb(string text, bool v, string tip) => new() { Content = text, IsChecked = v, Margin = new Thickness(0, 6, 0, 0), ToolTip = tip };
        var ocr = Cb("Taranmış sayfaların metnini önce tanı (OCR)", s.ConvertOcr,
            "Metni olmayan (taranmış) sayfalar dönüştürmeden önce tanınır. Açık belge değişmez; tanıma geçici bir kopyada yapılır.");
        var lang = Form.Combo(new[] { "Türkçe + İngilizce", "Türkçe", "İngilizce" }, Math.Max(0, Array.IndexOf(LangCodes, s.OcrLanguages)));
        var tables = Cb("Tabloları algıla", s.ConvertTables, "Çizgili tablolar gerçek tabloya (Word) veya hücrelere (Excel) dönüştürülür; birleşik hücreler korunur.");
        var images = Cb("Resimleri ekle", s.ConvertImages, "Sayfadaki fotoğraf ve çizimler belgeye resim olarak eklenir.");
        var perPage = Cb("Her sayfa ayrı çalışma sayfası", s.ConvertSheetPerPage, "Kapalıysa tüm sayfalar tek çalışma sayfasında alt alta yazılır.");
        var tablesOnly = Cb("Yalnızca tabloları aktar", s.ConvertTablesOnly, "Tablo dışındaki paragraflar atlanır.");
        var openAfter = Cb("Bitince dosyayı aç", s.ConvertOpenAfter, "Oluşturulan dosya varsayılan uygulamayla (Word, Excel, LibreOffice…) açılır.");
        var err = Error();
        var st = new StackPanel();
        st.Children.Add(Paragraph(ppt
            ? "Her sayfa bir slayta dönüşür: metinler özgün konumlarında düzenlenebilir metin kutuları, tablolar PowerPoint tablosu, resimler resim olarak aktarılır. Dönüşüm bu bilgisayarda yapılır."
            : word
            ? "Sayfa düzeni çözümlenir: başlıklar, paragraflar, hizalama, yazı tipi ve renkler, tablolar ve resimler düzenlenebilir bir Word belgesine aktarılır. Dönüşüm bu bilgisayarda yapılır."
            : "Tablolar hücrelere, sayılar (1.250,50 · %18 · 3.437,50 TL) gerçek sayılara dönüştürülür; hesaplamada doğrudan kullanılabilir. Dönüşüm bu bilgisayarda yapılır."));
        st.Children.Add(Form.Labeled($"Sayfalar (1–{pageCount})", box));
        st.Children.Add(Label("Seçenekler", 14));
        st.Children.Add(tables);
        if (word) st.Children.Add(images); else { st.Children.Add(perPage); st.Children.Add(tablesOnly); }
        st.Children.Add(ocr);
        var langRow = Form.Labeled("Belge dili", lang, 6);
        langRow.Margin = new Thickness(26, 0, 0, 0);
        st.Children.Add(langRow);
        if (scannedPages > 0)
            st.Children.Add(Hint($"Bu belgede metni olmayan {scannedPages} taranmış sayfa var. Tanıma kapalıysa bu sayfaların yalnız resmi aktarılır."));
        st.Children.Add(openAfter);
        st.Children.Add(err);
        ocr.Checked += (_, _) => lang.IsEnabled = true;
        ocr.Unchecked += (_, _) => lang.IsEnabled = false;
        lang.IsEnabled = ocr.IsChecked == true;
        d.Body = st;
        IReadOnlyList<int> pages = Array.Empty<int>();
        d.AddOkCancel("Dönüştür", () =>
        {
            bool ok = PageRange.TryParse(box.Text, pageCount, out pages, out var e);
            ShowError(err, e);
            return ok;
        });
        if (d.ShowDialog() != true) return null;
        s.ConvertOcr = ocr.IsChecked == true; s.ConvertTables = tables.IsChecked == true; s.ConvertImages = images.IsChecked == true;
        s.ConvertSheetPerPage = perPage.IsChecked == true; s.ConvertTablesOnly = tablesOnly.IsChecked == true; s.ConvertOpenAfter = openAfter.IsChecked == true;
        s.OcrLanguages = LangCodes[Math.Max(0, lang.SelectedIndex)];
        return new ConvertRequest(format, pages, s.ConvertOcr, OcrRunOptions.From(s), s.ConvertTables, s.ConvertImages, s.ConvertSheetPerPage, s.ConvertTablesOnly);
    }

    private ConvertDialog(string t) : base(t, 540) { }
}

/// <summary>PDF sıkıştırma seçenekleri.</summary>
public sealed class CompressDialog : DialogBase
{
    public static CompressOptions? Ask(string docName, long currentBytes, AppSettings s)
    {
        var d = new CompressDialog();
        var st = new StackPanel();
        st.Children.Add(Paragraph($"“{docName}” ({FormatSize(currentBytes)}) içindeki görseller seçilen çözünürlüğe küçültülüp yeniden kodlanır. Metin, yazı tipleri, bağlantılar ve yer imleri olduğu gibi kalır. Özgün dosya değiştirilmez; sıkıştırılmış belge yeni bir dosyaya kaydedilir."));
        var levels = new[]
        {
            Form.Radio("Yüksek kalite — 220 DPI, az kayıp (yazdırma için)", "cl", s.CompressLevel == 0),
            Form.Radio("Dengeli — 150 DPI (ekranda okuma ve e-posta için önerilen)", "cl", s.CompressLevel == 1),
            Form.Radio("En küçük dosya — 100 DPI, belirgin kayıp", "cl", s.CompressLevel == 2),
        };
        st.Children.Add(Label("Sıkıştırma düzeyi", 14));
        foreach (var r in levels) st.Children.Add(r);
        var gray = new CheckBox { Content = "Görselleri gri tonlamaya çevir", IsChecked = s.CompressGray, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Renkli taramalarda dosyayı ayrıca küçültür; renk bilgisi kaybolur." };
        st.Children.Add(gray);
        st.Children.Add(Hint("Saydam (maskeli) görseller ve siyah-beyaz taramalar (CCITT/JBIG2) zaten verimli olduğundan değiştirilmez. Sonuç özgün dosyadan küçük değilse dosya yazılmaz."));
        d.Body = st;
        d.AddOkCancel("Sıkıştır");
        if (d.ShowDialog() != true) return null;
        s.CompressLevel = Array.FindIndex(levels, r => r.IsChecked == true) is var i and >= 0 ? i : 1;
        s.CompressGray = gray.IsChecked == true;
        var o = s.CompressLevel switch { 0 => CompressOptions.High, 2 => CompressOptions.Small, _ => CompressOptions.Medium };
        return o with { Grayscale = s.CompressGray };
    }

    public static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} bayt",
    };

    private CompressDialog() : base("PDF'yi sıkıştır", 540) { }
}
