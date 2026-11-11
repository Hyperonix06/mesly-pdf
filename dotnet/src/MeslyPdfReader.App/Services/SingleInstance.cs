using System.IO;
using System.IO.Pipes;
using System.Text;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// Tek örnek çalışma: ikinci kez açılan uygulama, dosya yollarını adlandırılmış kanal üzerinden
/// çalışan örneğe iletir ve kapanır (Gezgin'de çift tıklama yeni sekme açar).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _name = "Mesly.PdfReader." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public event Action<string[]>? FilesReceived;

    /// <summary>Bu örnek ilk örnekse true. Değilse argümanlar ilk örneğe gönderilmiştir.</summary>
    public bool TryClaim(string[] args)
    {
        _mutex = new Mutex(true, _name, out bool created);
        if (created) return true;
        try
        {
            using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out);
            client.Connect(1500);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Select(Path.GetFullPath)) + "\n");
            client.Write(payload);
            client.Flush();
            return false;
        }
        catch
        {
            // İlk örnek yanıt vermiyorsa bu örnek bağımsız çalışsın.
            return true;
        }
    }

    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct);
                    var files = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    FilesReceived?.Invoke(files);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn("Tek örnek kanalı: " + ex.Message); await Task.Delay(500, CancellationToken.None); }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}
