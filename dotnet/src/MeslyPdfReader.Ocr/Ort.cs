using System.Runtime.InteropServices;
using System.Text;

namespace MeslyPdfReader.Ocr;

/// <summary>
/// ONNX Runtime 1.22 C API bağlamaları (onnxruntime_c_api.h, MIT). Yönetilen NuGet paketi yerine yalnızca yerel DLL kullanılır:
/// Windows'ta uygulama klasöründeki ocr\onnxruntime.dll (sistemdeki eski WinML kopyası değil), Linux'ta MESLY_ORT veya libonnxruntime.so.
/// Hiçbir veri ağa gönderilmez; çıkarım tamamen bu bilgisayarda yapılır.
/// </summary>
internal static unsafe class Ort
{
    public const uint ApiVersion = 22;

    // OrtApi işlev tablosu dizinleri (onnxruntime_c_api.h v1.22.1 sırası; GetApi(22) ile uyumlu).
    private const int GetErrorCode = 1, GetErrorMessage = 2, CreateEnvFn = 3, CreateSessionFromArrayFn = 8, RunFn = 9, CreateSessionOptionsFn = 10,
        DisableMemPatternFn = 17, SetSessionGraphOptimizationLevelFn = 23, SetIntraOpNumThreadsFn = 24, SetInterOpNumThreadsFn = 25,
        SessionGetInputCountFn = 30, SessionGetOutputCountFn = 31, SessionGetInputNameFn = 36, SessionGetOutputNameFn = 37,
        CreateTensorWithDataAsOrtValueFn = 49, GetTensorMutableDataFn = 51, GetDimensionsCountFn = 61, GetDimensionsFn = 62,
        GetTensorTypeAndShapeFn = 65, CreateCpuMemoryInfoFn = 69, AllocatorFreeFn = 76, GetAllocatorWithDefaultOptionsFn = 78,
        ReleaseEnvFn = 92, ReleaseStatusFn = 93, ReleaseMemoryInfoFn = 94, ReleaseSessionFn = 95, ReleaseValueFn = 96,
        ReleaseTensorTypeAndShapeInfoFn = 99, ReleaseSessionOptionsFn = 100;

    private static nint* _api;
    private static nint _env;
    private static string? _error;
    private static bool _initialized;
    private static readonly object Gate = new();

    /// <summary>Kütüphaneyi bir kez yükler. Başarısızsa kullanıcıya gösterilecek Türkçe neden döner.</summary>
    public static string? EnsureLoaded()
    {
        lock (Gate)
        {
            if (_initialized) return _error;
            _initialized = true;
            try
            {
                nint handle = 0;
                if (OperatingSystem.IsWindows())
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "ocr");
                    foreach (var rt in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
                        if (!NativeLibrary.TryLoad(rt, out _))
                        {
                            var local = Path.Combine(dir, rt);
                            if (File.Exists(local)) NativeLibrary.Load(local);
                        }
                    var path = Environment.GetEnvironmentVariable("MESLY_ORT") is { Length: > 0 } env && File.Exists(env) ? env : Path.Combine(dir, "onnxruntime.dll");
                    if (!File.Exists(path)) throw new DllNotFoundException("onnxruntime.dll bulunamadı");
                    handle = NativeLibrary.Load(path); // tam yol: System32'deki eski sürüm yüklenmez
                }
                else
                {
                    var candidates = new List<string?> { Environment.GetEnvironmentVariable("MESLY_ORT") };
                    var ocrDir = Path.Combine(AppContext.BaseDirectory, "ocr");
                    if (Directory.Exists(ocrDir)) candidates.AddRange(Directory.GetFiles(ocrDir, "libonnxruntime.so*"));
                    candidates.Add("libonnxruntime.so");
                    foreach (var c in candidates)
                        if (!string.IsNullOrEmpty(c) && NativeLibrary.TryLoad(c!, out handle)) break;
                    if (handle == 0) throw new DllNotFoundException("libonnxruntime.so bulunamadı");
                }
                var getBase = (delegate* unmanaged[Cdecl]<nint*>)NativeLibrary.GetExport(handle, "OrtGetApiBase");
                var apiBase = getBase();
                var getApi = (delegate* unmanaged[Cdecl]<uint, nint*>)apiBase[0];
                _api = getApi(ApiVersion);
                if (_api == null) throw new InvalidOperationException($"ONNX Runtime {ApiVersion} API sürümünü desteklemiyor");
                fixed (byte* id = "mesly\0"u8)
                {
                    nint env;
                    Check(((delegate* unmanaged[Cdecl]<int, byte*, nint*, nint>)_api[CreateEnvFn])(3, id, &env));
                    _env = env;
                }
                return _error = null;
            }
            catch (Exception ex)
            {
                return _error = $"Gelişmiş OCR (ONNX Runtime) yüklenemedi: {ex.Message}";
            }
        }
    }

    internal static void Check(nint status)
    {
        if (status == 0) return;
        var msg = Marshal.PtrToStringUTF8((nint)((delegate* unmanaged[Cdecl]<nint, byte*>)_api[GetErrorMessage])(status)) ?? "bilinmeyen hata";
        ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseStatusFn])(status);
        throw new OcrException("ONNX Runtime: " + msg);
    }

    /// <summary>Yüklenmiş bir ONNX modeli. Run eşzamanlı çağrılabilir.</summary>
    public sealed class Model : IDisposable
    {
        private nint _session, _memInfo;
        private nint _inName, _outName;
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public static Model Load(string path, int threads)
        {
            if (EnsureLoaded() is { } why) throw new OcrException(why);
            var bytes = File.ReadAllBytes(path);
            var meta = OnnxMetadata.Read(bytes);
            nint opts = 0, session = 0;
            try
            {
                Check(((delegate* unmanaged[Cdecl]<nint*, nint>)_api[CreateSessionOptionsFn])(&opts));
                Check(((delegate* unmanaged[Cdecl]<nint, int, nint>)_api[SetIntraOpNumThreadsFn])(opts, Math.Max(1, threads)));
                Check(((delegate* unmanaged[Cdecl]<nint, int, nint>)_api[SetInterOpNumThreadsFn])(opts, 1));
                Check(((delegate* unmanaged[Cdecl]<nint, int, nint>)_api[SetSessionGraphOptimizationLevelFn])(opts, 99)); // ORT_ENABLE_ALL
                Check(((delegate* unmanaged[Cdecl]<nint, nint>)_api[DisableMemPatternFn])(opts)); // girdi boyutları değişken
                fixed (byte* p = bytes)
                    Check(((delegate* unmanaged[Cdecl]<nint, void*, nuint, nint, nint*, nint>)_api[CreateSessionFromArrayFn])(_env, p, (nuint)bytes.Length, opts, &session));
            }
            finally { if (opts != 0) ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseSessionOptionsFn])(opts); }
            var m = new Model(session, meta);
            return m;
        }

        private Model(nint session, IReadOnlyDictionary<string, string> meta)
        {
            _session = session; Metadata = meta;
            nint alloc, mi;
            Check(((delegate* unmanaged[Cdecl]<nint*, nint>)_api[GetAllocatorWithDefaultOptionsFn])(&alloc));
            byte* n;
            Check(((delegate* unmanaged[Cdecl]<nint, nuint, nint, byte**, nint>)_api[SessionGetInputNameFn])(session, 0, alloc, &n));
            _inName = Marshal.StringToCoTaskMemUTF8(Marshal.PtrToStringUTF8((nint)n));
            ((delegate* unmanaged[Cdecl]<nint, void*, nint>)_api[AllocatorFreeFn])(alloc, n);
            Check(((delegate* unmanaged[Cdecl]<nint, nuint, nint, byte**, nint>)_api[SessionGetOutputNameFn])(session, 0, alloc, &n));
            _outName = Marshal.StringToCoTaskMemUTF8(Marshal.PtrToStringUTF8((nint)n));
            ((delegate* unmanaged[Cdecl]<nint, void*, nint>)_api[AllocatorFreeFn])(alloc, n);
            Check(((delegate* unmanaged[Cdecl]<int, int, nint*, nint>)_api[CreateCpuMemoryInfoFn])(1, 0, &mi)); // arena, varsayılan
            _memInfo = mi;
        }

        /// <summary>Tek float girdili, tek float çıktılı model çalıştırır.</summary>
        public (float[] Data, long[] Shape) Run(float[] input, long[] shape)
        {
            ObjectDisposedException.ThrowIf(_session == 0, this);
            long count = 1; foreach (var d in shape) count *= d;
            if (input.LongLength < count) throw new ArgumentException("Girdi dizisi şekle göre küçük.");
            nint inVal = 0, outVal = 0;
            try
            {
                fixed (float* p = input)
                fixed (long* sh = shape)
                {
                    Check(((delegate* unmanaged[Cdecl]<nint, void*, nuint, long*, nuint, int, nint*, nint>)_api[CreateTensorWithDataAsOrtValueFn])(
                        _memInfo, p, (nuint)(count * sizeof(float)), sh, (nuint)shape.Length, 1, &inVal));
                    nint inN = _inName, outN = _outName;
                    Check(((delegate* unmanaged[Cdecl]<nint, nint, nint*, nint*, nuint, nint*, nuint, nint*, nint>)_api[RunFn])(
                        _session, 0, &inN, &inVal, 1, &outN, 1, &outVal));
                }
                nint info = 0;
                try
                {
                    Check(((delegate* unmanaged[Cdecl]<nint, nint*, nint>)_api[GetTensorTypeAndShapeFn])(outVal, &info));
                    nuint dims;
                    Check(((delegate* unmanaged[Cdecl]<nint, nuint*, nint>)_api[GetDimensionsCountFn])(info, &dims));
                    var oshape = new long[(int)dims];
                    fixed (long* od = oshape) Check(((delegate* unmanaged[Cdecl]<nint, long*, nuint, nint>)_api[GetDimensionsFn])(info, od, dims));
                    long n = 1; foreach (var d in oshape) n *= d;
                    void* data;
                    Check(((delegate* unmanaged[Cdecl]<nint, void**, nint>)_api[GetTensorMutableDataFn])(outVal, &data));
                    var result = new float[n];
                    new ReadOnlySpan<float>(data, (int)n).CopyTo(result);
                    return (result, oshape);
                }
                finally { if (info != 0) ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseTensorTypeAndShapeInfoFn])(info); }
            }
            finally
            {
                if (outVal != 0) ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseValueFn])(outVal);
                if (inVal != 0) ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseValueFn])(inVal);
            }
        }

        public void Dispose()
        {
            if (_session == 0) return;
            ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseSessionFn])(_session);
            ((delegate* unmanaged[Cdecl]<nint, void>)_api[ReleaseMemoryInfoFn])(_memInfo);
            Marshal.FreeCoTaskMem(_inName); Marshal.FreeCoTaskMem(_outName);
            _session = 0;
        }
    }
}

/// <summary>ONNX dosyasının üst düzey özel üst verisini (metadata_props) protobuf'tan okur; PP-OCR tanıma modeli karakter sözlüğünü burada taşır.</summary>
internal static class OnnxMetadata
{
    public static IReadOnlyDictionary<string, string> Read(byte[] model)
    {
        var result = new Dictionary<string, string>();
        int pos = 0;
        while (pos < model.Length)
        {
            ulong tag = Varint(model, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: Varint(model, ref pos); break;
                case 1: pos += 8; break;
                case 5: pos += 4; break;
                case 2:
                {
                    int len = (int)Varint(model, ref pos);
                    if (field == 14) // ModelProto.metadata_props: StringStringEntryProto { 1: key, 2: value }
                    {
                        int p = pos, end = pos + len;
                        string? key = null, value = null;
                        while (p < end)
                        {
                            ulong t = Varint(model, ref p);
                            int f = (int)(t >> 3), w = (int)(t & 7);
                            if (w == 2)
                            {
                                int l = (int)Varint(model, ref p);
                                var s = Encoding.UTF8.GetString(model, p, l);
                                if (f == 1) key = s; else if (f == 2) value = s;
                                p += l;
                            }
                            else if (w == 0) Varint(model, ref p);
                            else break;
                        }
                        if (key is not null) result[key] = value ?? "";
                    }
                    pos += len;
                    break;
                }
                default: return result; // bozuk veri
            }
        }
        return result;
    }

    private static ulong Varint(byte[] b, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (pos < b.Length)
        {
            byte x = b[pos++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }
}
