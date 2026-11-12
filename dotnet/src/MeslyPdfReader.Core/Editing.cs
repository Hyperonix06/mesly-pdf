namespace MeslyPdfReader.Core;

public enum PageObjectKind { Unknown = 0, Text = 1, Path = 2, Image = 3, Shading = 4, Form = 5 }

public enum TextAlign { Left, Center, Right, Justify }

/// <summary>Sayfadaki bir içerik nesnesinin özeti. <see cref="Index"/>, belgenin o anki revizyonu için geçerlidir.</summary>
public sealed record PageObjectInfo(
    int Index, PageObjectKind Kind, RectF Bounds,
    string? Text = null, string? FontName = null, double FontSize = 0, uint FillRgb = 0, uint StrokeRgb = 0,
    bool Bold = false, bool Italic = false, bool Invisible = false, string? MeslyRole = null)
{
    public string KindName => Kind switch
    {
        PageObjectKind.Text => Invisible ? "Görünmez metin (OCR katmanı)" : "Metin",
        PageObjectKind.Path => "Şekil / çizgi",
        PageObjectKind.Image => "Görsel",
        PageObjectKind.Shading => "Renk geçişi",
        PageObjectKind.Form => "Nesne grubu (form XObject)",
        _ => "Nesne",
    };
}

/// <summary>Bir paragraf: aynı yazı tipinde, alt alta gelen metin satırları.</summary>
public sealed record TextParagraph(
    IReadOnlyList<int> ObjectIndices, RectF Bounds, string Text,
    string? FontName, double FontSize, uint Rgb, bool Bold, bool Italic,
    double FirstBaseline, double LineHeight, TextAlign Align,
    IReadOnlyList<TextLineInfo>? Lines = null, double RightLimit = 0, double Angle = 0, TextBlockInfo? Block = null);

/// <summary>
/// Mesly ile yazılmış bir paragrafın kutusu (metin nesnelerinde işaret olarak saklanır): hizalama, sütunun sol kenarı,
/// genişliği ve ilk satır girintisi. Paragraf yeniden düzenlenince tahmin yerine bu kullanılır; sonuç birebir tutarlıdır.
/// </summary>
public sealed record TextBlockInfo(TextAlign Align, double Left, double Width, double FirstIndent);

/// <summary>
/// Paragrafın özgün bir satırı: metni, başlangıç noktası (taban çizgisindeki ilk karakterin kökeni), taban çizgisi ve
/// mürekkebin sağ kenarı. Düzenlemede metnin yerinde kalması için kullanılır.
/// </summary>
public sealed record TextLineInfo(string Text, double X, double Baseline, double Right);

/// <summary>Yeni / değiştirilen metnin biçimi. Yazı tipi TrueType dosya verisi olarak verilir (gömülür).</summary>
/// <param name="UseOriginalFont">
/// Paragraf değiştirilirken belgenin gömülü özgün yazı tipi denenir (yeni metnin tüm karakterleri o yazı tipinde
/// varsa düzeltilen metin özgünüyle birebir aynı görünür); olmazsa <paramref name="FontData"/> kullanılır.
/// </param>
public sealed record TextStyle(byte[] FontData, double Size, uint Rgb = 0, TextAlign Align = TextAlign.Left, bool Underline = false, double LineSpacing = 1.2, byte Alpha = 255, bool UseOriginalFont = false);

public enum ShapeKind { Rectangle, Ellipse, Line, Arrow }

public sealed record ShapeStyle(uint StrokeRgb = 0x000000, double StrokeWidth = 1.5, uint? FillRgb = null, byte Alpha = 255);

public enum AlignMode { Left, CenterH, Right, Top, Middle, Bottom }

public enum HFPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>Üst/altbilgi metni. {sayfa}, {toplam}, {tarih}, {dosya} yer tutucuları desteklenir.</summary>
public sealed record HeaderFooterItem(HFPosition Position, string Template);

public sealed record HeaderFooterSettings(IReadOnlyList<HeaderFooterItem> Items, TextStyle Style, double MarginPt = 28, int StartNumber = 1, string? FileName = null);

public sealed record WatermarkSettings(string Text, TextStyle Style, double AngleDegrees = 45, double Opacity = 0.18);

/// <summary>Metin satırlarına bölme (sözcük kaydırma). Ölçüm işlevi dışarıdan verilir (PDF motoru ile ölçülür).</summary>
public static class TextLayout
{
    public static IReadOnlyList<string> Wrap(string text, double maxWidth, Func<string, double> measure) =>
        WrapEx(text, maxWidth, maxWidth, measure).Select(l => l.Text).ToList();

    /// <summary>
    /// Satır kırma; ilk satır farklı genişlikte olabilir (girinti). <c>EndsParagraph</c>: satır bir paragrafın (kullanıcının
    /// Enter ile ayırdığı bölümün) son satırıdır — iki yana yaslamada son satır yaslanmaz.
    /// </summary>
    public static IReadOnlyList<(string Text, bool EndsParagraph)> WrapEx(string text, double firstWidth, double restWidth, Func<string, double> measure)
    {
        var lines = new List<(string, bool)>();
        double Width() => lines.Count == 0 ? firstWidth : restWidth;
        foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var words = para.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { lines.Add(("", true)); continue; }
            var cur = "";
            foreach (var w in words)
            {
                var candidate = cur.Length == 0 ? w : cur + " " + w;
                if (cur.Length > 0 && measure(candidate) > Width())
                {
                    lines.Add((cur, false));
                    cur = w;
                }
                else cur = candidate;
                // Tek sözcük bile sığmıyorsa karakter karakter böl.
                while (measure(cur) > Width() && cur.Length > 1)
                {
                    int cut = cur.Length - 1;
                    while (cut > 1 && measure(cur[..cut]) > Width()) cut--;
                    lines.Add((cur[..cut], false));
                    cur = cur[cut..];
                }
            }
            lines.Add((cur, true));
        }
        return lines;
    }

    /// <summary>Şablondaki yer tutucuları doldurur.</summary>
    public static string Fill(string template, int page, int total, string? fileName, DateTime date) => template
        .Replace("{sayfa}", page.ToString(), StringComparison.OrdinalIgnoreCase)
        .Replace("{toplam}", total.ToString(), StringComparison.OrdinalIgnoreCase)
        .Replace("{tarih}", date.ToString("dd.MM.yyyy", TextSearch.Turkish), StringComparison.OrdinalIgnoreCase)
        .Replace("{dosya}", fileName ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>"ABCDEF+Arial-BoldMT" → ("Arial", bold, italic).</summary>
    public static (string Family, bool Bold, bool Italic) ParseFontName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", false, false);
        var n = name;
        int plus = n.IndexOf('+');
        if (plus == 6) n = n[(plus + 1)..];
        var lower = n.ToLowerInvariant();
        bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold");
        bool italic = lower.Contains("italic") || lower.Contains("oblique");
        var family = n.Split('-', ',')[0];
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (family.EndsWith(suffix, StringComparison.Ordinal) && family.Length > suffix.Length + 2) { family = family[..^suffix.Length]; break; }
        // "TimesNewRoman" → "Times New Roman"
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < family.Length; i++)
        {
            if (i > 0 && char.IsUpper(family[i]) && char.IsLower(family[i - 1])) sb.Append(' ');
            sb.Append(family[i]);
        }
        return (sb.ToString().Trim(), bold, italic);
    }
}
