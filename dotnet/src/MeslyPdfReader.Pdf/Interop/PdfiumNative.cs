using System.Runtime.InteropServices;

namespace MeslyPdfReader.Pdf.Interop;

#pragma warning disable IDE1006 // PDFium adlandırması korunur

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF { public float left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF { public float width, height; }

[StructLayout(LayoutKind.Sequential)]
internal struct FS_MATRIX { public float a, b, c, d, e, f; }

/// <summary>FPDF_FILEACCESS: m_FileLen, m_GetBlock, m_Param.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEACCESS
{
    public CULong m_FileLen;
    public delegate* unmanaged[Cdecl]<nint, CULong, byte*, CULong, int> m_GetBlock;
    public nint m_Param;
}

/// <summary>FPDF_FILEWRITE + kendi bağlam alanımız (yapının sonuna eklenir, PDFium görmez).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE_EX
{
    public int version;
    public delegate* unmanaged[Cdecl]<FPDF_FILEWRITE_EX*, void*, CULong, int> WriteBlock;
    public nint context;
}

internal static unsafe partial class Native
{
    private const string Lib = "pdfium";

    // Hata kodları
    public const int FPDF_ERR_SUCCESS = 0, FPDF_ERR_UNKNOWN = 1, FPDF_ERR_FILE = 2, FPDF_ERR_FORMAT = 3,
        FPDF_ERR_PASSWORD = 4, FPDF_ERR_SECURITY = 5, FPDF_ERR_PAGE = 6;

    // Çizim bayrakları
    public const int FPDF_ANNOT = 0x01, FPDF_LCD_TEXT = 0x02, FPDF_GRAYSCALE = 0x08, FPDF_REVERSE_BYTE_ORDER = 0x10,
        FPDF_RENDER_LIMITEDIMAGECACHE = 0x200, FPDF_PRINTING = 0x800;

    public const int FPDFBitmap_BGRA = 4, FPDFBitmap_BGR = 2;

    // Arama
    public const uint FPDF_MATCHCASE = 1, FPDF_MATCHWHOLEWORD = 2;

    // Eylem türleri
    public const uint PDFACTION_GOTO = 1, PDFACTION_REMOTEGOTO = 2, PDFACTION_URI = 3, PDFACTION_LAUNCH = 4;

    // Kaydetme
    public const uint FPDF_INCREMENTAL = 1, FPDF_NO_INCREMENTAL = 2, FPDF_REMOVE_SECURITY = 4, FPDF_SUBSET_NEW_FONTS = 8;

    public const int FPDF_FONT_TRUETYPE = 2;

    // --- fpdfview.h ---
    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial void FPDF_DestroyLibrary();
    [LibraryImport(Lib)] public static partial CULong FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial nint FPDF_LoadCustomDocument(FPDF_FILEACCESS* access, byte* password);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadMemDocument64(void* data, nuint size, byte* password);
    [LibraryImport(Lib)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(nint doc, out int version);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(nint doc);
    [LibraryImport(Lib)] public static partial CULong FPDF_GetDocPermissions(nint doc);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(nint doc, int index, out FS_SIZEF size);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageBoundingBox(nint page, out FS_RECTF rect);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, CULong color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Lib)] public static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    [LibraryImport(Lib)] public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [LibraryImport(Lib)] public static partial CULong FPDF_GetMetaText(nint doc, byte* tag, void* buffer, CULong buflen);
    [LibraryImport(Lib)] public static partial CULong FPDF_GetPageLabel(nint doc, int index, void* buffer, CULong buflen);

    // --- fpdf_text.h ---
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(nint textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(nint textPage);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetLooseCharBox(nint textPage, int index, out FS_RECTF rect);
    [LibraryImport(Lib)] public static partial int FPDFText_GetCharBox(nint textPage, int index, out double left, out double right, out double bottom, out double top);
    [LibraryImport(Lib)] public static partial int FPDFText_IsGenerated(nint textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetText(nint textPage, int start, int count, ushort* result);
    [LibraryImport(Lib)] public static partial int FPDFText_CountRects(nint textPage, int start, int count);
    [LibraryImport(Lib)] public static partial int FPDFText_GetRect(nint textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // --- fpdf_doc.h ---
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetFirstChild(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetNextSibling(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial CULong FPDFBookmark_GetTitle(nint bookmark, void* buffer, CULong buflen);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetDest(nint doc, nint bookmark);
    [LibraryImport(Lib)] public static partial nint FPDFBookmark_GetAction(nint bookmark);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(nint doc, nint dest);
    [LibraryImport(Lib)] public static partial CULong FPDFAction_GetType(nint action);
    [LibraryImport(Lib)] public static partial nint FPDFAction_GetDest(nint doc, nint action);
    [LibraryImport(Lib)] public static partial CULong FPDFAction_GetURIPath(nint doc, nint action, void* buffer, CULong buflen);
    [LibraryImport(Lib)] public static partial int FPDFLink_Enumerate(nint page, ref int startPos, out nint link);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetAnnotRect(nint link, out FS_RECTF rect);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetDest(nint doc, nint link);
    [LibraryImport(Lib)] public static partial nint FPDFLink_GetAction(nint link);

    // --- fpdf_edit.h / fpdf_ppo.h / fpdf_save.h / fpdf_transformpage.h ---
    [LibraryImport(Lib)] public static partial nint FPDFPage_New(nint doc, int index, double width, double height);
    [LibraryImport(Lib)] public static partial void FPDFPage_Delete(nint doc, int index);
    [LibraryImport(Lib)] public static partial int FPDF_MovePages(nint doc, int* indices, CULong length, int destIndex);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetRotation(nint page, int rotate);
    [LibraryImport(Lib)] public static partial int FPDF_ImportPagesByIndex(nint dest, nint src, int* indices, CULong length, int index);
    [LibraryImport(Lib)] public static partial int FPDF_CopyViewerPreferences(nint dest, nint src);
    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(nint doc, FPDF_FILEWRITE_EX* writer, CULong flags);
    [LibraryImport(Lib)] public static partial int FPDF_SaveWithVersion(nint doc, FPDF_FILEWRITE_EX* writer, CULong flags, int version);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetMediaBox(nint page, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Lib)] public static partial int FPDFPage_GetCropBox(nint page, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetMediaBox(nint page, float left, float bottom, float right, float top);
    [LibraryImport(Lib)] public static partial void FPDFPage_SetCropBox(nint page, float left, float bottom, float right, float top);
    [LibraryImport(Lib)] public static partial int FPDFPage_TransFormWithClip(nint page, FS_MATRIX* matrix, FS_RECTF* clip);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Lib)] public static partial int FPDFPage_InsertObject(nint page, nint obj);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_NewImageObj(nint doc);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_LoadJpegFileInline(nint* pages, int count, nint imageObj, FPDF_FILEACCESS* access);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObj, nint bitmap);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_GetImagePixelSize(nint imageObj, out uint width, out uint height);
    [LibraryImport(Lib)] public static partial int FPDFImageObj_SetMatrix(nint imageObj, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Destroy(nint obj);
    [LibraryImport(Lib)] public static partial void FPDFPageObj_Transform(nint obj, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetFillColor(nint obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial nint FPDFText_LoadFont(nint doc, byte* data, uint size, int fontType, int cid);
    [LibraryImport(Lib)] public static partial void FPDFFont_Close(nint font);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateTextObj(nint doc, nint font, float size);
    [LibraryImport(Lib)] public static partial int FPDFText_SetText(nint textObj, ushort* text);
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateNewRect(float x, float y, float w, float h);
    [LibraryImport(Lib)] public static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);
}
