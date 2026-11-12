using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MeslyPdfReader.App.Services;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, long InstallerSize, string Notes);

/// <summary>
/// Yeni sürüm denetimi: GitHub'daki Hyperonix06/mesly-pdf deposunun en son sürümü (etiket “mpr-vX.Y.Z”) sorulur.
/// Yalnız sürüm bilgisi alınır; hiçbir belge veya kişisel veri gönderilmez. İndirme ve kurulum kullanıcı onayıyla yapılır.
/// </summary>
public static class UpdateService
{
    public const string Repo = "Hyperonix06/mesly-pdf";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public static Version Current => typeof(UpdateService).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"MeslyPdfReader/{Current}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Daha yeni sürüm varsa bilgisini, yoksa null döner. Ağ hatası istisna fırlatır (çağıran gösterir).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // henüz yayımlanmış sürüm yok
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct), Current);
    }

    /// <summary>GitHub “releases/latest” yanıtını çözer; <paramref name="current"/>'ten yeni değilse null.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var ver) || ver <= current) return null;
        string? url = null; long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            // Önce tam ad (MeslyPdfReader-Setup-X.Y.Z.exe), yoksa herhangi bir kurulum dosyası.
            var exact = $"MeslyPdfReader-Setup-{ver}.exe";
            foreach (var pass in new[] { true, false })
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    bool match = pass ? name.Equals(exact, StringComparison.OrdinalIgnoreCase)
                        : name.StartsWith("MeslyPdfReader-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    if (match) { url = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64(); break; }
                }
                if (url is not null) break;
            }
        }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(ver, tag, page, url, size, notes);
    }

    public static bool TryParseTag(string tag, out Version v)
    {
        var t = tag.Trim();
        foreach (var p in new[] { "mpr-v", "v" }) if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { t = t[p.Length..]; break; }
        if (Version.TryParse(t, out var parsed)) { v = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build)); return true; }
        v = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Kurulum dosyasını geçici klasöre indirir.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.InstallerUrl is null) throw new InvalidOperationException("Bu sürümde kurulum dosyası yok; sürüm sayfasından indirin.");
        Directory.CreateDirectory(AppPaths.Temp);
        var path = Path.Combine(AppPaths.Temp, $"MeslyPdfReader-Setup-{info.Version}.exe");
        using var resp = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.InstallerSize;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (info.InstallerSize > 0 && new FileInfo(path).Length != info.InstallerSize)
        {
            File.Delete(path);
            throw new IOException("İndirilen dosyanın boyutu beklenenden farklı; indirme yarım kalmış olabilir. Lütfen yeniden deneyin.");
        }
        return path;
    }

    /// <summary>Kurulumu başlatır (uygulama kapanınca kurulum yeni sürümü yükler).</summary>
    public static void RunInstaller(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
