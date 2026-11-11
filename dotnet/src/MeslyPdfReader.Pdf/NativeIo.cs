using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>
/// .NET akışını PDFium'a FPDF_FILEACCESS olarak sunar. PDFium dosyayı gerektikçe parça parça okur;
/// böylece çok büyük PDF'ler belleğe tamamen alınmaz.
/// </summary>
internal sealed unsafe class StreamSource : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private GCHandle _handle;
    public FPDF_FILEACCESS* Access { get; private set; }

    public StreamSource(Stream stream, bool ownsStream = true)
    {
        if (!stream.CanSeek || !stream.CanRead) throw new ArgumentException("Akış okunabilir ve konumlandırılabilir olmalı.");
        if (stream.Length > uint.MaxValue && OperatingSystem.IsWindows())
            throw new Core.PdfException(Core.PdfErrorKind.Unsupported, "4 GB'tan büyük PDF dosyaları desteklenmiyor.");
        _stream = stream;
        _ownsStream = ownsStream;
        _handle = GCHandle.Alloc(this);
        Access = (FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEACCESS));
        Access->m_FileLen = new CULong((nuint)stream.Length);
        Access->m_GetBlock = &GetBlock;
        Access->m_Param = GCHandle.ToIntPtr(_handle);
    }

    public long Length => _stream.Length;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBlock(nint param, CULong position, byte* buffer, CULong size)
    {
        try
        {
            var self = (StreamSource)GCHandle.FromIntPtr(param).Target!;
            long pos = (long)position.Value;
            int len = checked((int)size.Value);
            var span = new Span<byte>(buffer, len);
            self._stream.Position = pos;
            int total = 0;
            while (total < len)
            {
                int n = self._stream.Read(span[total..]);
                if (n <= 0) return 0;
                total += n;
            }
            return 1;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (Access != null) { NativeMemory.Free(Access); Access = null; }
        if (_handle.IsAllocated) _handle.Free();
        if (_ownsStream) _stream.Dispose();
    }
}

/// <summary>FPDF_SaveAsCopy çıktısını bir .NET akışına yazar.</summary>
internal static unsafe class StreamWriterBridge
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int WriteBlock(FPDF_FILEWRITE_EX* self, void* data, CULong size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(self->context).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch { return 0; }
    }

    public static bool Save(nint doc, Stream output, uint flags, int version = 0)
    {
        var handle = GCHandle.Alloc(output);
        try
        {
            FPDF_FILEWRITE_EX w;
            w.version = 1;
            w.WriteBlock = &WriteBlock;
            w.context = GCHandle.ToIntPtr(handle);
            int ok = version > 0
                ? Native.FPDF_SaveWithVersion(doc, &w, new CULong(flags), version)
                : Native.FPDF_SaveAsCopy(doc, &w, new CULong(flags));
            return ok != 0;
        }
        finally { handle.Free(); }
    }
}

internal static unsafe class NativeStrings
{
    /// <summary>UTF-16LE tampon döndüren PDFium fonksiyonları için (bayt uzunluğu, sonlandırıcı dahil).</summary>
    public static string ReadUtf16(Func<nint, uint, uint> call)
    {
        uint len = call(0, 0);
        if (len <= 2) return "";
        var buf = new byte[len];
        fixed (byte* p = buf) call((nint)p, len);
        var s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)len - 2);
        int z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    public static string ReadAscii(Func<nint, uint, uint> call)
    {
        uint len = call(0, 0);
        if (len <= 1) return "";
        var buf = new byte[len];
        fixed (byte* p = buf) call((nint)p, len);
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)len - 1);
    }

    public static byte[] Utf8Z(string? s) => s is null ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(s + "\0");
}
