using System.Collections.Concurrent;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>
/// PDFium iş parçacığı güvenli değildir. Tüm PDFium çağrıları bu sınıfın sahip olduğu tek
/// bir iş parçacığında, iki öncelikli kuyruktan (etkileşimli / arka plan) sırayla yürütülür.
/// </summary>
public sealed class PdfEngine
{
    private static readonly Lazy<PdfEngine> _instance = new(() => new PdfEngine(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static PdfEngine Instance => _instance.Value;

    private readonly BlockingCollection<Action>[] _queues = { new(), new() };
    private readonly Thread _thread;
    private int _threadId = -1;
    private long _executed;

    public long ExecutedCount => Interlocked.Read(ref _executed);
    public int PendingCount => _queues[0].Count + _queues[1].Count;
    public bool IsEngineThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Motor iş parçacığında yakalanan, kullanıcıya iletilmemiş hatalar için.</summary>
    public event Action<Exception>? UnhandledError;

    private PdfEngine()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Environment.CurrentManagedThreadId;
            Native.FPDF_InitLibrary();
            ready.Set();
            Loop();
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "Mesly PDF motoru" };
        _thread.Start();
        ready.Wait();
    }

    private void Loop()
    {
        while (true)
        {
            Action? work;
            try { BlockingCollection<Action>.TakeFromAny(_queues, out work); }
            catch (ObjectDisposedException) { return; }
            try { work!(); }
            catch (Exception ex) { UnhandledError?.Invoke(ex); }
            Interlocked.Increment(ref _executed);
        }
    }

    public Task<T> RunAsync<T>(Func<T> func, CancellationToken ct = default, bool lowPriority = false)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        if (IsEngineThread)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queues[lowPriority ? 1 : 0].Add(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(func()); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAsync(Action action, CancellationToken ct = default, bool lowPriority = false)
        => RunAsync<bool>(() => { action(); return true; }, ct, lowPriority);

    /// <summary>Eşzamanlı çağrı. Motor iş parçacığındaysa doğrudan çalıştırır.</summary>
    public T Invoke<T>(Func<T> func) => IsEngineThread ? func() : RunAsync(func).GetAwaiter().GetResult();

    public void Invoke(Action action) => Invoke<bool>(() => { action(); return true; });
}
