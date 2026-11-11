using System.Reflection;
using System.Runtime.InteropServices;

namespace MeslyPdfReader.Ocr;

/// <summary>Tesseract 5 C API (capi.h) bağlamaları. Windows'ta uygulama klasöründeki ocr\tesseract50.dll, Linux'ta libtesseract.so.5.</summary>
internal static unsafe partial class Tess
{
    private const string Lib = "mesly_tesseract";

    public const int PSM_OSD_ONLY = 0, PSM_AUTO_OSD = 1, PSM_AUTO = 3, PSM_SINGLE_BLOCK = 6;
    public const int RIL_BLOCK = 0, RIL_PARA = 1, RIL_TEXTLINE = 2, RIL_WORD = 3, RIL_SYMBOL = 4;

    private static string? _loadError;
    private static bool _resolverSet;
    private static readonly object Gate = new();

    /// <summary>Yerel kütüphaneyi bir kez yükler. Başarısızsa nedeni döner.</summary>
    public static string? EnsureLoaded()
    {
        lock (Gate)
        {
            if (_resolverSet) return _loadError;
            _resolverSet = true;
            nint handle = 0;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "ocr");
                    // VC++ çalışma zamanı: sistemde varsa o, yoksa uygulamayla gelen kopya.
                    foreach (var rt in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
                        if (!NativeLibrary.TryLoad(rt, out _))
                        {
                            var local = Path.Combine(dir, rt);
                            if (File.Exists(local)) NativeLibrary.Load(local);
                        }
                    NativeLibrary.Load(Path.Combine(dir, "leptonica-1.82.0.dll"));
                    handle = NativeLibrary.Load(Path.Combine(dir, "tesseract50.dll"));
                }
                else
                {
                    foreach (var n in new[] { "libtesseract.so.5", "libtesseract.so" })
                        if (NativeLibrary.TryLoad(n, out handle)) break;
                    if (handle == 0) throw new DllNotFoundException("libtesseract.so.5 bulunamadı");
                }
                var h = handle;
                NativeLibrary.SetDllImportResolver(typeof(Tess).Assembly, (name, _, _) => name == Lib ? h : 0);
                return _loadError = null;
            }
            catch (Exception ex)
            {
                return _loadError = $"OCR motoru yüklenemedi: {ex.Message}";
            }
        }
    }

    [LibraryImport(Lib)] public static partial byte* TessVersion();
    [LibraryImport(Lib)] public static partial nint TessBaseAPICreate();
    [LibraryImport(Lib)] public static partial void TessBaseAPIDelete(nint api);
    [LibraryImport(Lib)] public static partial void TessBaseAPIEnd(nint api);
    [LibraryImport(Lib)] public static partial void TessBaseAPIClear(nint api);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int TessBaseAPIInit3(nint api, string datapath, string language);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int TessBaseAPISetVariable(nint api, string name, string value);
    [LibraryImport(Lib)] public static partial void TessBaseAPISetPageSegMode(nint api, int mode);
    [LibraryImport(Lib)] public static partial void TessBaseAPISetImage(nint api, byte* data, int width, int height, int bytesPerPixel, int bytesPerLine);
    [LibraryImport(Lib)] public static partial void TessBaseAPISetSourceResolution(nint api, int ppi);
    [LibraryImport(Lib)] public static partial int TessBaseAPIRecognize(nint api, nint monitor);
    [LibraryImport(Lib)] public static partial nint TessBaseAPIGetIterator(nint api);
    [LibraryImport(Lib)] public static partial int TessBaseAPIDetectOrientationScript(nint api, out int orientDeg, out float orientConf, out nint scriptName, out float scriptConf);
    [LibraryImport(Lib)] public static partial void TessDeleteText(byte* text);

    [LibraryImport(Lib)] public static partial void TessResultIteratorDelete(nint it);
    [LibraryImport(Lib)] public static partial int TessResultIteratorNext(nint it, int level);
    [LibraryImport(Lib)] public static partial byte* TessResultIteratorGetUTF8Text(nint it, int level);
    [LibraryImport(Lib)] public static partial float TessResultIteratorConfidence(nint it, int level);
    [LibraryImport(Lib)] public static partial nint TessResultIteratorGetPageIteratorConst(nint it);
    [LibraryImport(Lib)] public static partial int TessPageIteratorBoundingBox(nint pit, int level, out int left, out int top, out int right, out int bottom);
    [LibraryImport(Lib)] public static partial int TessPageIteratorIsAtBeginningOf(nint pit, int level);
    [LibraryImport(Lib)] public static partial int TessPageIteratorBaseline(nint pit, int level, out int x1, out int y1, out int x2, out int y2);

    [LibraryImport(Lib)] public static partial nint TessMonitorCreate();
    [LibraryImport(Lib)] public static partial void TessMonitorDelete(nint monitor);
    [LibraryImport(Lib)] public static partial void TessMonitorSetCancelFunc(nint monitor, delegate* unmanaged[Cdecl]<nint, int, int> cancel);
    [LibraryImport(Lib)] public static partial void TessMonitorSetCancelThis(nint monitor, nint cancelThis);
    [LibraryImport(Lib)] public static partial int TessMonitorGetProgress(nint monitor);

    public static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);
}
