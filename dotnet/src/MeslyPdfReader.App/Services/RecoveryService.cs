using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Data;

namespace MeslyPdfReader.App.Services;

public sealed record RecoveryEntry(string Id, string RecoveryFile, string? OriginalPath, string DisplayName, DateTime SavedAt, int PageCount, int Pid = 0);

/// <summary>
/// Otomatik kurtarma: değiştirilmiş belgelerin kopyası belirli aralıklarla kurtarma klasörüne yazılır.
/// Uygulama düzgün kapanırsa kopyalar silinir; beklenmedik kapanmada sonraki açılışta geri yüklenmesi önerilir.
/// </summary>
public sealed class RecoveryService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private Func<IEnumerable<DocumentViewModel>>? _source;
    private readonly Dictionary<Guid, int> _savedRevision = new();
    private bool _saving;

    public RecoveryService(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(settings.AutoRecoveryMinutes) };
        _timer.Tick += async (_, _) => await SaveAllAsync();
    }

    public void Start(Func<IEnumerable<DocumentViewModel>> source)
    {
        _source = source;
        _timer.Interval = TimeSpan.FromMinutes(_settings.AutoRecoveryMinutes);
        _timer.IsEnabled = _settings.AutoRecovery;
    }

    public void Refresh()
    {
        _timer.Interval = TimeSpan.FromMinutes(_settings.AutoRecoveryMinutes);
        _timer.IsEnabled = _settings.AutoRecovery && _source is not null;
    }

    public async Task SaveAllAsync()
    {
        if (_saving || _source is null || !_settings.AutoRecovery) return;
        _saving = true;
        try
        {
            Directory.CreateDirectory(AppPaths.Recovery);
            foreach (var vm in _source().ToList())
            {
                if (!vm.Document.IsModified) continue;
                if (_savedRevision.TryGetValue(vm.Id, out var rev) && rev == vm.Document.Revision) continue;
                var file = Path.Combine(AppPaths.Recovery, vm.Id.ToString("N") + ".pdf");
                try
                {
                    await vm.Document.SaveCopyAsync(file);
                    var entry = new RecoveryEntry(vm.Id.ToString("N"), file, vm.Document.FilePath, vm.Title, DateTime.Now, vm.PageCount, Environment.ProcessId);
                    await File.WriteAllTextAsync(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(entry));
                    _savedRevision[vm.Id] = vm.Document.Revision;
                }
                catch (Exception ex) { Log.Warn($"Kurtarma kopyası yazılamadı ({vm.Title}): {ex.Message}"); }
            }
        }
        finally { _saving = false; }
    }

    /// <summary>Belge kaydedildi veya kapatıldı: kurtarma kopyasını sil.</summary>
    public void Forget(DocumentViewModel vm)
    {
        _savedRevision.Remove(vm.Id);
        var baseFile = Path.Combine(AppPaths.Recovery, vm.Id.ToString("N"));
        foreach (var ext in new[] { ".pdf", ".json" }) try { File.Delete(baseFile + ext); } catch { }
    }

    public static IReadOnlyList<RecoveryEntry> FindLeftovers()
    {
        var list = new List<RecoveryEntry>();
        try
        {
            if (!Directory.Exists(AppPaths.Recovery)) return list;
            foreach (var json in Directory.EnumerateFiles(AppPaths.Recovery, "*.json"))
            {
                try
                {
                    var e = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllText(json));
                    if (e is not null && IsLiveOtherWindow(e.Pid)) continue; // “Yeni Pencere” ile açılmış, hâlâ çalışan başka pencere
                    if (e is not null && File.Exists(e.RecoveryFile)) list.Add(e);
                    else File.Delete(json);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(e => e.SavedAt).ToList();
    }

    private static bool IsLiveOtherWindow(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Discard(RecoveryEntry e)
    {
        try { File.Delete(e.RecoveryFile); } catch { }
        try { File.Delete(Path.ChangeExtension(e.RecoveryFile, ".json")); } catch { }
    }

    public void Dispose() => _timer.Stop();
}
