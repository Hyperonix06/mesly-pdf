namespace MeslyPdfReader.Core;

/// <summary>Sistemde kurulu bir TrueType yazı tipi dosyası.</summary>
public sealed record InstalledFont(string Path, string Family, bool Bold, bool Italic, Func<char, bool> HasGlyph);

/// <summary>Seçilen yazı tipi ve neden seçildiği (kullanıcıya gösterilir).</summary>
public sealed record FontChoice(InstalledFont? Font, bool Exact, string? Note);

/// <summary>
/// PDF içindeki yazı tipi adını (ör. “ABCDEF+TimesNewRomanPS-BoldMT”) kurulu yazı tiplerine eşler.
/// PDF'e gömülü alt küme yazı tipleri yeni karakterleri içermeyebileceğinden düzenlenen metin
/// her zaman kurulu tam bir yazı tipiyle yeniden yazılır.
/// </summary>
public static class FontMatching
{
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["helvetica"] = "arial", ["helveticaneue"] = "arial", ["arialunicode"] = "arial",
        ["times"] = "timesnewroman", ["timesroman"] = "timesnewroman", ["timesnewromanps"] = "timesnewroman",
        ["courier"] = "couriernew", ["couriernewps"] = "couriernew",
        ["calibrilight"] = "calibrilight", ["segoe"] = "segoeui",
    };

    /// <summary>Karşılaştırma anahtarı: küçük harf, yalnız harf/rakam, PS/MT/Std sonekleri atılmış.</summary>
    public static string Key(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return "";
        var s = new string(family.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var suf in new[] { "psmt", "mt", "ps", "std" })
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length + 3) { s = s[..^suf.Length]; break; }
        return Aliases.TryGetValue(s, out var a) ? a : s;
    }

    /// <summary>
    /// En uygun yazı tipini seçer: (1) aynı aile + aynı kalınlık/eğiklik, (2) aynı aile, (3) adı benzeyen aile,
    /// (4) yedekler (Arial, Segoe UI, DejaVu Sans…). Metindeki tüm karakterleri içermeyen yazı tipleri atlanır.
    /// </summary>
    public static FontChoice Choose(IReadOnlyList<InstalledFont> fonts, string? pdfFontName, string text, bool bold, bool italic,
        string? preferredFamily = null, IEnumerable<string>? fallbacks = null)
    {
        string wanted = preferredFamily ?? TextLayout.ParseFontName(pdfFontName).Family;
        string key = Key(wanted);
        var needed = text.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).Distinct().ToArray();
        bool Covers(InstalledFont f) => needed.All(f.HasGlyph);
        InstalledFont? Best(IEnumerable<InstalledFont> cands) => cands
            .OrderBy(f => (f.Bold == bold ? 0 : 2) + (f.Italic == italic ? 0 : 1))
            .FirstOrDefault(Covers);

        string? missingNote = null;
        if (key.Length > 0)
        {
            var same = fonts.Where(f => Key(f.Family) == key).ToList();
            if (same.Count > 0)
            {
                var f = Best(same);
                if (f is not null) return new FontChoice(f, f.Bold == bold && f.Italic == italic, null);
                missingNote = $"“{wanted}” yazı tipi metindeki bazı karakterleri içermiyor";
            }
            else
            {
                var similar = fonts.Where(f => { var k = Key(f.Family); return k.Length >= 4 && (k.StartsWith(key) || key.StartsWith(k)); }).ToList();
                var s = Best(similar);
                if (s is not null) return new FontChoice(s, false, $"“{wanted}” bulunamadı; benzer “{s.Family}” kullanıldı.");
                missingNote = $"“{wanted}” bu bilgisayarda kurulu değil";
            }
        }
        foreach (var fb in fallbacks ?? new[] { "Arial", "Segoe UI", "Calibri", "DejaVu Sans" })
        {
            var f = Best(fonts.Where(x => Key(x.Family) == Key(fb)));
            if (f is not null) return new FontChoice(f, false, missingNote is null ? null : $"{missingNote}; “{f.Family}” kullanıldı.");
        }
        var any = fonts.FirstOrDefault(Covers);
        return new FontChoice(any, false, any is null ? "Metindeki karakterleri içeren bir yazı tipi bulunamadı." : $"{missingNote ?? "Uygun yazı tipi yok"}; “{any.Family}” kullanıldı.");
    }
}
