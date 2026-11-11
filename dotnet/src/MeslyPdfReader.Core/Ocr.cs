namespace MeslyPdfReader.Core;

/// <summary>Tanınan bir sözcük. Koordinatlar görüntü pikseli (sol-üst köşe başlangıç).</summary>
public sealed record OcrWord(string Text, int Left, int Top, int Right, int Bottom, float Confidence, int Line, int Block, int BaselineY)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Bir sayfanın OCR sonucu.</summary>
public sealed record OcrPage(int Width, int Height, int Dpi, IReadOnlyList<OcrWord> Words, string Languages)
{
    public double MeanConfidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);

    /// <summary>Satır ve blok yapısı korunarak düz metin.</summary>
    public string Text
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            int line = -1, block = -1;
            foreach (var w in Words)
            {
                if (w.Block != block && block >= 0) sb.Append("\n\n");
                else if (w.Line != line && line >= 0) sb.Append('\n');
                else if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Text);
                line = w.Line; block = w.Block;
            }
            return sb.ToString();
        }
    }
}

/// <summary>OCR dili.</summary>
public sealed record OcrLanguage(string Code, string Name)
{
    public static IReadOnlyList<OcrLanguage> Known { get; } = new[]
    {
        new OcrLanguage("tur", "Türkçe"), new OcrLanguage("eng", "İngilizce"),
        new OcrLanguage("deu", "Almanca"), new OcrLanguage("fra", "Fransızca"), new OcrLanguage("ara", "Arapça"), new OcrLanguage("rus", "Rusça"),
    };

    public static string NameOf(string code) => Known.FirstOrDefault(l => l.Code == code)?.Name ?? code;
}

/// <summary>OCR seçenekleri.</summary>
public sealed record OcrOptions(string Languages = "tur+eng", int Dpi = 300, ImageCorrection? Correction = null, float LowConfidence = 60);
