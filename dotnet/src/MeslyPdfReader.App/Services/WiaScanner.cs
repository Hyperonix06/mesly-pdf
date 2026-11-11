using System.IO;
using System.Runtime.InteropServices;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// Windows Görüntü Alma (WIA 2.0, wiaaut.dll — Windows 10/11'de yerleşik) ile tarama.
/// Düz cam, belge besleyici ve çift taraflı besleyici desteklenir. COM nesneleri yalnızca
/// kendi STA iş parçacığında kullanılır.
/// </summary>
public sealed class WiaScanner : IScannerBackend
{
    // Cihaz özellikleri
    private const int DevName = 7, DevManufacturer = 3;
    private const int DpsDocumentHandlingCapabilities = 3086, DpsDocumentHandlingStatus = 3087, DpsDocumentHandlingSelect = 3088, DpsPages = 3096;
    private const int DpsHorizontalBedSize = 3074, DpsVerticalBedSize = 3075, DpsHorizontalSheetFeedSize = 3076, DpsVerticalSheetFeedSize = 3077;
    // Öğe (sayfa) özellikleri
    private const int IpsCurIntent = 6146, IpsXRes = 6147, IpsYRes = 6148, IpsXPos = 6149, IpsYPos = 6150, IpsXExtent = 6151, IpsYExtent = 6152;
    private const int IpsBrightness = 6154, IpsContrast = 6155;
    // Bayraklar
    private const int Feeder = 1, Flatbed = 2, Duplex = 4;
    private const int IntentColor = 1, IntentGray = 2, IntentText = 4;

    private const string FormatJpeg = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    private const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string FormatTiff = "{B96B3CB1-0728-11D3-9D7B-0000F81EF32E}";

    private dynamic? _device, _item;
    private ScanSettings? _settings;
    private string _format = FormatJpeg;

    // ─────────────── STA çalıştırıcı ───────────────

    /// <summary>İşi ayrı bir STA iş parçacığında çalıştırır (WIA COM gereksinimi).</summary>
    public static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(Translate(ex)); }
        }) { IsBackground = true, Name = "Mesly tarama (WIA)" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        if (type is null) throw new ScanException(ScanErrorKind.ServiceUnavailable, "WIA kitaplığı (wiaaut.dll) bulunamadı");
        try { return Activator.CreateInstance(type)!; }
        catch (COMException ex) { throw new ScanException(ScanErrorKind.ServiceUnavailable, $"0x{ex.HResult:X8}", ex); }
    }

    // ─────────────── Cihazlar ───────────────

    /// <summary>Bağlı tarayıcıları listeler (cihaz yoksa boş liste).</summary>
    public static Task<IReadOnlyList<ScannerInfo>> ListAsync() => RunStaAsync<IReadOnlyList<ScannerInfo>>(() =>
    {
        var list = new List<ScannerInfo>();
        dynamic manager = CreateCom("WIA.DeviceManager");
        foreach (dynamic info in manager.DeviceInfos)
        {
            try
            {
                if ((int)info.Type != 1) continue; // 1 = ScannerDeviceType
                string id = info.DeviceID;
                string name = Prop(info.Properties, DevName)?.ToString() ?? "Tarayıcı";
                string? man = Prop(info.Properties, DevManufacturer)?.ToString();
                list.Add(new ScannerInfo(id, name, man));
            }
            catch (Exception ex) { Log.Warn("WIA cihaz bilgisi okunamadı: " + ex.Message); }
        }
        return list;
    });

    public static Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId) => RunStaAsync(() =>
    {
        dynamic device = Connect(deviceId);
        int caps = ToInt(Prop(device.Properties, DpsDocumentHandlingCapabilities));
        int status = ToInt(Prop(device.Properties, DpsDocumentHandlingStatus));
        int maxDpi = 600;
        foreach (dynamic it in device.Items)
        {
            try { maxDpi = ToInt(PropObject(it.Properties, IpsXRes)?.SubTypeMax, 600); } catch { }
            break;
        }
        // Yetenek bildirmeyen sürücüler yalnız düz cam kabul edilir.
        bool flat = caps == 0 || (caps & Flatbed) != 0;
        return new ScannerCapabilities(flat, (caps & Feeder) != 0, (caps & Duplex) != 0, (status & Feeder) != 0, maxDpi);
    });

    private static dynamic Connect(string deviceId)
    {
        dynamic manager = CreateCom("WIA.DeviceManager");
        foreach (dynamic info in manager.DeviceInfos)
            if ((string)info.DeviceID == deviceId)
            {
                try { return info.Connect(); }
                catch (COMException ex) { throw Translate(ex); }
            }
        throw new ScanException(ScanErrorKind.NoDevice);
    }

    // ─────────────── IScannerBackend ───────────────

    public void Begin(string deviceId, ScanSettings s)
    {
        _settings = s;
        _device = Connect(deviceId);
        foreach (dynamic it in _device.Items) { _item = it; break; }
        if (_item is null) throw new ScanException(ScanErrorKind.Unsupported, "tarayıcı öğesi yok");

        // Bazı sürücüler cihaz özelliği değişince sayfa ayarlarını sıfırlar: önce öğe, sonra cihaz özellikleri,
        // en sonda öğe özellikleri bir kez daha uygulanır.
        ApplyItemProperties(s);
        int select = s.Source switch { ScanSource.Flatbed => Flatbed, ScanSource.FeederDuplex => Feeder | Duplex, _ => Feeder };
        if (!TrySet(_device.Properties, DpsDocumentHandlingSelect, select) && s.Source != ScanSource.Flatbed)
            throw new ScanException(ScanErrorKind.Unsupported, "belge besleyici seçilemedi");
        if (s.Source != ScanSource.Flatbed) TrySet(_device.Properties, DpsPages, 1);
        ApplyItemProperties(s);
        _format = s.Color == ScanColorMode.BlackWhite ? FormatPng : FormatJpeg;
    }

    private void ApplyItemProperties(ScanSettings s)
    {
        dynamic props = _item!.Properties;
        int intent = s.Color switch { ScanColorMode.Gray => IntentGray, ScanColorMode.BlackWhite => IntentText, _ => IntentColor };
        TrySet(props, IpsCurIntent, intent);
        TrySet(props, IpsXRes, s.Dpi);
        TrySet(props, IpsYRes, s.Dpi);
        TrySet(props, IpsXPos, 0);
        TrySet(props, IpsYPos, 0);
        int maxW = ToInt(PropObject(props, IpsXExtent)?.SubTypeMax), maxH = ToInt(PropObject(props, IpsYExtent)?.SubTypeMax);
        var (w, h) = ScanJob.ExtentPixels(s.Paper, s.Dpi, maxW, maxH);
        TrySet(props, IpsXExtent, w);
        TrySet(props, IpsYExtent, h);
        if (s.Brightness != 0) TrySet(props, IpsBrightness, s.Brightness);
        if (s.Contrast != 0) TrySet(props, IpsContrast, s.Contrast);
    }

    public ScannedPage AcquireNext()
    {
        dynamic image;
        try { image = _item!.Transfer(_format); }
        catch (COMException ex) when (IsFormatProblem(ex) && _format != FormatBmp)
        {
            // Sürücü istenen biçimi vermiyorsa BMP ile yeniden dene.
            _format = FormatBmp;
            try { image = _item!.Transfer(_format); }
            catch (COMException ex2) { throw Translate(ex2); }
        }
        catch (COMException ex) { throw Translate(ex); }
        catch (Exception ex) when (ex is not ScanException) { throw Translate(ex); }

        byte[] data = ReadImage(image);
        string fid = ((string)image.FormatID).ToUpperInvariant();
        var fmt = fid == FormatJpeg ? ScannedFormat.Jpeg : fid == FormatPng ? ScannedFormat.Png : fid == FormatTiff ? ScannedFormat.Tiff : ScannedFormat.Bmp;
        return new ScannedPage { Data = data, Format = fmt, Dpi = _settings!.Dpi };
    }

    public void End()
    {
        if (_item is not null) { try { Marshal.FinalReleaseComObject(_item); } catch { } _item = null; }
        if (_device is not null) { try { Marshal.FinalReleaseComObject(_device); } catch { } _device = null; }
    }

    private static byte[] ReadImage(dynamic image)
    {
        try
        {
            object bin = image.FileData.BinaryData;
            if (bin is byte[] b && b.Length > 0) return b;
        }
        catch { }
        var tmp = Path.Combine(AppPaths.Temp, $"tarama-{Guid.NewGuid():N}.img");
        try { image.SaveFile(tmp); return File.ReadAllBytes(tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Tarayıcının kendi (sürücü) tarama penceresiyle tek sayfa alır.</summary>
    public static Task<ScannedPage?> AcquireWithDriverDialogAsync() => RunStaAsync<ScannedPage?>(() =>
    {
        dynamic dlg = CreateCom("WIA.CommonDialog");
        dynamic? img;
        try { img = dlg.ShowAcquireImage(1, 1, 131072, FormatJpeg, false, true, false); }
        catch (COMException ex) { throw Translate(ex); }
        if (img is null) return null;
        string fid = ((string)img.FormatID).ToUpperInvariant();
        return new ScannedPage
        {
            Data = ReadImage(img),
            Format = fid == FormatJpeg ? ScannedFormat.Jpeg : fid == FormatPng ? ScannedFormat.Png : fid == FormatTiff ? ScannedFormat.Tiff : ScannedFormat.Bmp,
            Dpi = ToInt((object?)img.HorizontalResolution, 300),
        };
    });

    // ─────────────── Yardımcılar ───────────────

    private static dynamic? PropObject(dynamic props, int id)
    {
        foreach (dynamic p in props) if ((int)p.PropertyID == id) return p;
        return null;
    }

    private static object? Prop(dynamic props, int id)
    {
        var p = PropObject(props, id);
        try { return p?.Value; } catch { return null; }
    }

    private static bool TrySet(dynamic props, int id, int value)
    {
        var p = PropObject(props, id);
        if (p is null) return false;
        try { p.Value = value; return true; }
        catch (Exception ex) { Log.Warn($"WIA özelliği {id}={value} ayarlanamadı: {ex.Message}"); return false; }
    }

    private static int ToInt(object? v, int def = 0)
    {
        try { return v is null ? def : System.Convert.ToInt32(v); } catch { return def; }
    }

    private static bool IsFormatProblem(COMException ex) => ex.HResult is unchecked((int)0x80070057) or unchecked((int)0x80004001) or unchecked((int)0x8021000B);

    public static Exception Translate(Exception ex)
    {
        if (ex is ScanException) return ex;
        var c = ex as COMException ?? ex.InnerException as COMException;
        if (c is null) return new ScanException(ScanErrorKind.Other, ex.Message, ex);
        var kind = unchecked((uint)c.HResult) switch
        {
            0x80210002 => ScanErrorKind.PaperJam,
            0x80210003 => ScanErrorKind.PaperEmpty,
            0x80210004 or 0x80210020 => ScanErrorKind.PaperProblem,
            0x80210005 or 0x8021000A => ScanErrorKind.Offline,
            0x80210006 or 0x8021000D => ScanErrorKind.Busy,
            0x80210007 => ScanErrorKind.WarmingUp,
            0x80210015 => ScanErrorKind.NoDevice,
            0x80210016 => ScanErrorKind.CoverOpen,
            0x80210064 => ScanErrorKind.Cancelled,
            0x80040154 or 0x800706BA or 0x80070422 => ScanErrorKind.ServiceUnavailable,
            _ => ScanErrorKind.Other,
        };
        return new ScanException(kind, kind == ScanErrorKind.Other ? $"0x{c.HResult:X8} {c.Message}" : null, ex);
    }
}
