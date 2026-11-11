using System.Globalization;

namespace MeslyPdfReader.Core;

/// <summary>
/// Kullanıcının yazdığı sayfa aralığını ("1-3, 5, 8-", "tek", "çift", "son", "tümü") 0 tabanlı indekslere çevirir.
/// </summary>
public static class PageRange
{
    public static IReadOnlyList<int> Parse(string? text, int pageCount, bool allowDuplicates = false)
    {
        if (pageCount <= 0) throw new FormatException("Belgede sayfa yok.");
        var s = (text ?? "").Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        if (s is "" or "tümü" or "tumu" or "hepsi" or "*" or "all")
            return Enumerable.Range(0, pageCount).ToArray();
        if (s is "tek" or "tek sayfalar")
            return Enumerable.Range(0, pageCount).Where(i => i % 2 == 0).ToArray();
        if (s is "çift" or "cift" or "çift sayfalar")
            return Enumerable.Range(0, pageCount).Where(i => i % 2 == 1).ToArray();

        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var raw in s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            int a, b;
            if (part.Contains('-'))
            {
                var pieces = part.Split('-', 2);
                a = pieces[0].Length == 0 ? 1 : ParseNum(pieces[0], pageCount, part);
                b = pieces[1].Length == 0 ? pageCount : ParseNum(pieces[1], pageCount, part);
            }
            else a = b = ParseNum(part, pageCount, part);

            if (a < 1 || b < 1 || a > pageCount || b > pageCount)
                throw new FormatException($"“{part}” geçersiz: sayfalar 1 ile {pageCount} arasında olmalı.");
            int step = a <= b ? 1 : -1;
            for (int p = a; ; p += step)
            {
                if (allowDuplicates || seen.Add(p - 1)) result.Add(p - 1);
                if (p == b) break;
            }
        }
        if (result.Count == 0) throw new FormatException("Sayfa aralığı boş.");
        return result;
    }

    public static bool TryParse(string? text, int pageCount, out IReadOnlyList<int> pages, out string? error)
    {
        try { pages = Parse(text, pageCount); error = null; return true; }
        catch (FormatException ex) { pages = Array.Empty<int>(); error = ex.Message; return false; }
    }

    private static int ParseNum(string t, int pageCount, string part)
    {
        if (t is "son" or "last") return pageCount;
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        throw new FormatException($"“{part}” anlaşılamadı. Örnek: 1-3, 5, 8-son");
    }

    /// <summary>0 tabanlı indeksleri kısa metne çevirir: [0,1,2,4] → "1-3, 5".</summary>
    public static string Format(IEnumerable<int> indices)
    {
        var sorted = indices.Distinct().OrderBy(i => i).ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i] + 1}" : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
