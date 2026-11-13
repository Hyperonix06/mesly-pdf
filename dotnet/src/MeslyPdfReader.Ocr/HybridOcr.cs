using MeslyPdfReader.Core;

namespace MeslyPdfReader.Ocr;

/// <summary>
/// Gelişmiş (PP-OCRv6) ve Tesseract sonuçlarını sözcük düzeyinde birleştirir.
/// PP-OCRv6 gürültülü/soluk/gölgeli taramada çok daha dayanıklıdır ama Türkçe noktalı-noktasız harflerde (İ/ı/i, Ğ, Ş) hata yapabilir;
/// Tesseract'ın Türkçe sözlüğü temiz metinde bu harfleri doğru verir. Aynı konumdaki sözcükte iki motor yalnızca benzer görünen harflerde ayrışıyorsa
/// Tesseract'ın güvenli okuması alınır; gerçek bir ayrılıkta yalnızca Tesseract çok eminse ve gelişmiş motor kesin değilse alınır.
/// Tesseract'ın bulduğu ama gelişmiş motorun kaçırdığı yüksek güvenli sözcükler eklenir.
/// </summary>
public static class HybridOcr
{
    private const float AcceptConfusable = 50, AcceptDifferent = 90, KeepAdvancedAbove = 98, AddMissing = 90;

    // Birbirine benzeyen harf grupları (küçük harfe katlanmış): aynı gruptaki harfler arasındaki farkın maliyeti sıfırdır.
    private static readonly string[] Groups =
    {
        "iıİIl1|!", "ğgčćĞG", "şsš$ŞSŠ5", "çcčćÇC", "öoÖO0", "üuÜU", "ž", "aáâä",
    };

    private static bool Similar(char a, char b)
    {
        if (a == b) return true;
        char la = char.ToLowerInvariant(a), lb = char.ToLowerInvariant(b);
        if (la == lb) return true;
        foreach (var g in Groups) if (g.Contains(a) && g.Contains(b)) return true;
        foreach (var g in Groups) if (g.Contains(la) && g.Contains(lb)) return true;
        return false;
    }

    /// <summary>Benzer harf değişimleri bedelsiz Levenshtein mesafesi.</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (Similar(a[i - 1], b[j - 1]) ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static double Overlap(OcrWord a, OcrWord b)
    {
        int ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (ox <= 0 || oy <= 0) return 0;
        if (oy < 0.5 * Math.Min(a.Height, b.Height)) return 0;
        return (double)ox / Math.Min(a.Width, b.Width);
    }

    public sealed record Result(OcrPage Page, int Replaced, int Added);

    public static Result Merge(OcrPage advanced, OcrPage tesseract)
    {
        var ws = advanced.Words.ToList();
        var used = new HashSet<int>();
        int replaced = 0, added = 0;
        var tw = tesseract.Words;

        for (int i = 0; i < ws.Count; i++)
        {
            var p = ws[i];
            int bestJ = -1; double best = 0;
            for (int j = 0; j < tw.Count; j++)
            {
                if (used.Contains(j)) continue;
                double o = Overlap(p, tw[j]);
                // Karşılıklı örtüşme: Tesseract sözcüğü de gelişmiş sözcüğün büyük kısmını kaplamalı (birleşik/bölünmüş sözcükler karıştırılmaz).
                if (o > best && Overlap(tw[j], p) >= 0.5 && Math.Abs(p.Width - tw[j].Width) <= 0.6 * Math.Max(p.Width, tw[j].Width)) { best = o; bestJ = j; }
            }
            if (bestJ < 0 || best < 0.5) continue;
            var t = tw[bestJ];
            used.Add(bestJ);
            if (string.Equals(p.Text, t.Text, StringComparison.Ordinal)) continue;
            int dist = Distance(p.Text, t.Text);
            bool take = dist == 0 ? t.Confidence >= AcceptConfusable
                                  : t.Confidence >= AcceptDifferent && p.Confidence < KeepAdvancedAbove && dist <= Math.Max(1, p.Text.Length / 2);
            if (take) { ws[i] = t with { Line = p.Line, Block = p.Block }; replaced++; }
        }

        // Gelişmiş motorun hiç görmediği, Tesseract'ın çok emin olduğu sözcükler.
        for (int j = 0; j < tw.Count; j++)
        {
            if (used.Contains(j) || tw[j].Confidence < AddMissing || tw[j].Text.Length < 2) continue;
            var t = tw[j];
            if (ws.Any(p => Overlap(p, t) > 0.2 || Overlap(t, p) > 0.2)) continue;
            var host = ws.Where(p => Math.Min(p.Bottom, t.Bottom) - Math.Max(p.Top, t.Top) >= 0.5 * Math.Min(p.Height, t.Height))
                         .OrderBy(p => Math.Abs(p.Left - t.Left)).FirstOrDefault();
            if (host is null) continue; // satırı olmayan yalnız sözcük eklenmez (okuma sırası bozulmasın)
            ws.Add(t with { Line = host.Line, Block = host.Block }); added++;
        }
        var ordered = ws.OrderBy(w => w.Block).ThenBy(w => w.Line).ThenBy(w => w.Left).ToList();
        return new Result(advanced with { Words = ordered }, replaced, added);
    }
}
