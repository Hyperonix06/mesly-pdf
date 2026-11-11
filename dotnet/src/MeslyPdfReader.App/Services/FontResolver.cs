using System.IO;
using System.Windows.Media;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// Windows'ta kurulu TrueType yazı tiplerini (sistem + kullanıcı) dizinler ve PDF yazı tipi adlarını bunlara eşler.
/// Dizin ilk kullanımda arka planda bir kez oluşturulur. Uygulamayla gelen DejaVu Sans her zaman listededir.
/// </summary>
public sealed class FontResolver
{
    public static FontResolver Instance { get; } = new();
    private Task<IReadOnlyList<InstalledFont>>? _index;
    private readonly Dictionary<string, byte[]> _data = new(StringComparer.OrdinalIgnoreCase);

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "Assets", "DejaVuSans.ttf");

    public Task<IReadOnlyList<InstalledFont>> GetFontsAsync() => _index ??= Task.Run(BuildIndex);

    /// <summary>Panelde gösterilecek aile adları (alfabetik).</summary>
    public async Task<IReadOnlyList<string>> GetFamiliesAsync() =>
        (await GetFontsAsync()).Select(f => f.Family).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Create(TextSearch.Turkish, true)).ToList();

    public async Task<(byte[] Data, FontChoice Choice)> ResolveAsync(string? pdfFontName, string text, bool bold, bool italic, string? preferredFamily = null)
    {
        var fonts = await GetFontsAsync();
        var choice = FontMatching.Choose(fonts, pdfFontName, text, bold, italic, preferredFamily);
        var path = choice.Font?.Path ?? BundledPath;
        return (Load(path), choice);
    }

    public byte[] Load(string path)
    {
        lock (_data)
        {
            if (!_data.TryGetValue(path, out var d)) _data[path] = d = File.ReadAllBytes(path);
            return d;
        }
    }

    private static IReadOnlyList<InstalledFont> BuildIndex()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
        };
        var list = new List<InstalledFont>();
        void Add(string file)
        {
            try
            {
                var gt = new GlyphTypeface(new Uri(file));
                var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
                string family = gt.Win32FamilyNames.TryGetValue(en, out var w) ? w : gt.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file);
                // Win32 aile adı “Arial Black” gibi kalınlık içerebilir; tipografik aile adı varsa onu kullan.
                if (gt.FamilyNames.TryGetValue(en, out var typo) && !string.IsNullOrWhiteSpace(typo)) family = typo;
                bool bold = gt.Weight.ToOpenTypeWeight() >= 600;
                bool italic = gt.Style != System.Windows.FontStyles.Normal;
                var map = gt.CharacterToGlyphMap;
                list.Add(new InstalledFont(file, family, bold, italic, c => map.ContainsKey(c)));
            }
            catch (Exception ex) { Log.Info($"Yazı tipi okunamadı ({Path.GetFileName(file)}): {ex.Message}"); }
        }
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*.ttf")) Add(f);
        if (File.Exists(BundledPath)) Add(BundledPath);
        Log.Info($"Yazı tipi dizini: {list.Count} yazı tipi, {sw.ElapsedMilliseconds} ms");
        return list;
    }
}
