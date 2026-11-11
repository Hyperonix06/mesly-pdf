using System.Globalization;
using System.Text;

namespace MeslyPdfReader.Core;

/// <summary>Türkçe kurallarına uygun (İ/i, I/ı) metin arama.</summary>
public static class TextSearch
{
    public static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static IEnumerable<(int Index, int Length)> FindAll(string text, string query, SearchOptions options)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) yield break;

        if (options.IgnoreDiacritics)
        {
            // Türkçe harmanlamada ç, ğ, ı, ö, ş, ü ayrı harflerdir; "aksanları yok say" için
            // karakterler bire bir (konum korunarak) temel harflerine katlanır.
            var ft = Fold(text, options.MatchCase);
            var fq = Fold(query, options.MatchCase);
            int s = 0;
            while (s < ft.Length)
            {
                int idx = ft.IndexOf(fq, s, StringComparison.Ordinal);
                if (idx < 0) yield break;
                if (!options.WholeWord || IsWholeWord(text, idx, fq.Length)) yield return (idx, fq.Length);
                s = idx + Math.Max(1, fq.Length);
            }
            yield break;
        }

        var cmp = Turkish.CompareInfo;
        var opts = options.MatchCase ? CompareOptions.None : CompareOptions.IgnoreCase;
        int start = 0;
        while (start < text.Length)
        {
            int rel = cmp.IndexOf(text.AsSpan(start), query.AsSpan(), opts, out int matchLength);
            if (rel < 0 || matchLength <= 0) yield break;
            int idx = start + rel;
            if (!options.WholeWord || IsWholeWord(text, idx, matchLength))
                yield return (idx, matchLength);
            start = idx + Math.Max(1, matchLength);
        }
    }

    /// <summary>Uzunluğu değiştirmeden aksanları kaldırır (ve istenirse Türkçe küçük harfe çevirir).</summary>
    public static string Fold(string s, bool keepCase = false)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!keepCase) c = char.ToLower(c, Turkish);
            chars[i] = c switch
            {
                'ç' => 'c', 'Ç' => 'C', 'ğ' => 'g', 'Ğ' => 'G', 'ı' => 'i', 'İ' => keepCase ? 'I' : 'i',
                'ö' => 'o', 'Ö' => 'O', 'ş' => 's', 'Ş' => 'S', 'ü' => 'u', 'Ü' => 'U',
                _ => BaseLetter(c),
            };
        }
        return new string(chars);
    }

    private static char BaseLetter(char c)
    {
        if (c < 0x80) return c;
        var d = c.ToString().Normalize(NormalizationForm.FormD);
        return d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c;
    }

    private static bool IsWholeWord(string text, int idx, int len)
    {
        bool before = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
        bool after = idx + len >= text.Length || !char.IsLetterOrDigit(text[idx + len]);
        return before && after;
    }

    /// <summary>Eşleşmenin çevresinden tek satırlık bağlam metni üretir.</summary>
    public static string Context(string text, int idx, int len, int radius = 40)
    {
        int s = Math.Max(0, idx - radius), e = Math.Min(text.Length, idx + len + radius);
        var snippet = text[s..e].Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (snippet.Contains("  ")) snippet = snippet.Replace("  ", " ");
        return (s > 0 ? "…" : "") + snippet.Trim() + (e < text.Length ? "…" : "");
    }
}
