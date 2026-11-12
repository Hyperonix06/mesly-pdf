using System.Runtime.InteropServices;
using System.Text;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

public enum AnnotKind { Note, Highlight, Underline, StrikeOut, Squiggly, FreeText, Ink, Shape, Stamp, Other }

/// <summary>Sayfadaki bir açıklama (bağlantı ve form alanları hariç). Rect PDF koordinatı.</summary>
public sealed record AnnotationInfo(int Page, int Index, AnnotKind Kind, RectF Rect, string Contents, uint Rgb)
{
    public string KindName => Kind switch
    {
        AnnotKind.Note => "Not", AnnotKind.Highlight => "Vurgu", AnnotKind.Underline => "Altı çizili", AnnotKind.StrikeOut => "Üstü çizili",
        AnnotKind.Squiggly => "Dalgalı çizgi", AnnotKind.FreeText => "Metin kutusu", AnnotKind.Ink => "Serbest çizim", AnnotKind.Shape => "Şekil",
        AnnotKind.Stamp => "Damga", _ => "Açıklama",
    };
}

public enum FormFieldKind { Text, CheckBox, Radio, ComboBox, ListBox, PushButton, Signature, Unknown }

/// <summary>Form alanı (her pencere öğesi ayrı satır; radyo düğmelerinde dışa aktarım değeriyle).</summary>
public sealed record FormFieldInfo(int Page, int AnnotIndex, string Name, FormFieldKind Kind, string Value, bool Checked, string ExportValue,
    IReadOnlyList<string> Options, int SelectedOption, RectF Rect, bool ReadOnly);

/// <summary>Form alanına yazılacak yeni değer. Text: metin; CheckBox/Radio: işaret; ComboBox/ListBox: seçenek sırası.</summary>
public sealed record FormFieldChange(FormFieldInfo Field, string? Text = null, bool? Checked = null, int? Option = null);

public sealed unsafe partial class PdfDocument
{
    // ─────────────── Açıklamalar ───────────────

    private static AnnotKind KindOf(int subtype) => subtype switch
    {
        Native.FPDF_ANNOT_TEXT => AnnotKind.Note,
        Native.FPDF_ANNOT_HIGHLIGHT => AnnotKind.Highlight,
        Native.FPDF_ANNOT_UNDERLINE => AnnotKind.Underline,
        Native.FPDF_ANNOT_STRIKEOUT => AnnotKind.StrikeOut,
        Native.FPDF_ANNOT_SQUIGGLY => AnnotKind.Squiggly,
        Native.FPDF_ANNOT_FREETEXT => AnnotKind.FreeText,
        Native.FPDF_ANNOT_INK => AnnotKind.Ink,
        Native.FPDF_ANNOT_SQUARE or Native.FPDF_ANNOT_CIRCLE => AnnotKind.Shape,
        Native.FPDF_ANNOT_STAMP => AnnotKind.Stamp,
        _ => AnnotKind.Other,
    };

    /// <summary>Kullanıcıya listelenen türler: bağlantı, form alanı ve açılır pencere (popup) hariç.</summary>
    private static bool IsUserAnnot(int subtype) => subtype is not (Native.FPDF_ANNOT_LINK or Native.FPDF_ANNOT_WIDGET or Native.FPDF_ANNOT_POPUP) && subtype > 0;

    /// <summary>
    /// Seçili metnin satırlarını işaretler (vurgu, altı çizili, üstü çizili). Görünüm (appearance) PDFium tarafından
    /// standart biçimde üretilir; her PDF okuyucusunda aynı görünür ve açıklama olarak düzenlenebilir/silinebilir kalır.
    /// </summary>
    public Task AddMarkupAsync(int pageIndex, AnnotKind kind, IReadOnlyList<RectF> lineRects, uint rgb, string? contents = null) => _engine.RunAsync(() =>
    {
        if (lineRects.Count == 0) throw new ArgumentException("İşaretlenecek metin yok.");
        int subtype = kind switch
        {
            AnnotKind.Highlight => Native.FPDF_ANNOT_HIGHLIGHT,
            AnnotKind.Underline => Native.FPDF_ANNOT_UNDERLINE,
            AnnotKind.StrikeOut => Native.FPDF_ANNOT_STRIKEOUT,
            AnnotKind.Squiggly => Native.FPDF_ANNOT_SQUIGGLY,
            _ => throw new ArgumentException("Desteklenmeyen işaretleme türü."),
        };
        var page = Page(pageIndex);
        var annot = Native.FPDFPage_CreateAnnot(page, subtype);
        if (annot == 0) throw new PdfException(PdfErrorKind.Unknown, "Açıklama oluşturulamadı.");
        try
        {
            RectF bounds = default;
            foreach (var raw in lineRects)
            {
                var r = raw.Normalize();
                // Sıra: sol-üst, sağ-üst, sol-alt, sağ-alt (Acrobat ile uyumlu).
                var q = new FS_QUADPOINTSF
                {
                    x1 = (float)r.Left, y1 = (float)r.Top, x2 = (float)r.Right, y2 = (float)r.Top,
                    x3 = (float)r.Left, y3 = (float)r.Bottom, x4 = (float)r.Right, y4 = (float)r.Bottom,
                };
                if (Native.FPDFAnnot_AppendAttachmentPoints(annot, &q) == 0) throw new PdfException(PdfErrorKind.Unknown, "İşaretleme noktaları yazılamadı.");
                bounds = bounds.IsEmpty ? r : bounds.Union(r);
            }
            var fr = new FS_RECTF { left = (float)bounds.Left, bottom = (float)bounds.Bottom, right = (float)bounds.Right, top = (float)bounds.Top };
            Native.FPDFAnnot_SetRect(annot, &fr);
            SetColor(annot, rgb);
            Native.FPDFAnnot_SetFlags(annot, Native.FPDF_ANNOT_FLAG_PRINT);
            SetString(annot, "T", Environment.UserName);
            SetString(annot, "M", PdfDate(DateTime.Now));
            if (!string.IsNullOrEmpty(contents)) SetString(annot, "Contents", contents);
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    });

    /// <summary>Yapışkan not (Text açıklaması): sayfada not simgesi, içerik açıklama metni.</summary>
    public Task AddNoteAsync(int pageIndex, double x, double y, string text, uint rgb) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var annot = Native.FPDFPage_CreateAnnot(page, Native.FPDF_ANNOT_TEXT);
        if (annot == 0) throw new PdfException(PdfErrorKind.Unknown, "Not oluşturulamadı.");
        try
        {
            var fr = new FS_RECTF { left = (float)x, top = (float)y, right = (float)(x + 20), bottom = (float)(y - 20) };
            Native.FPDFAnnot_SetRect(annot, &fr);
            SetColor(annot, rgb);
            Native.FPDFAnnot_SetFlags(annot, Native.FPDF_ANNOT_FLAG_PRINT | 8 /* NoZoom */ | 16 /* NoRotate */);
            SetString(annot, "Contents", text);
            SetString(annot, "T", Environment.UserName);
            SetString(annot, "M", PdfDate(DateTime.Now));
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    });

    public Task<IReadOnlyList<AnnotationInfo>> GetAnnotationsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var list = new List<AnnotationInfo>();
        int n = Native.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < n && list.Count < 2000; i++)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            try
            {
                int st = Native.FPDFAnnot_GetSubtype(a);
                if (!IsUserAnnot(st) || Native.FPDFAnnot_GetRect(a, out var r) == 0) continue;
                uint rgb = Native.FPDFAnnot_GetColor(a, Native.FPDFANNOT_COLORTYPE_Color, out var cr, out var cg, out var cb, out _) != 0 ? (cr << 16) | (cg << 8) | cb : 0xFFE14D;
                list.Add(new AnnotationInfo(pageIndex, i, KindOf(st), new RectF(r.left, r.bottom, r.right, r.top).Normalize(), GetString(a, "Contents"), rgb));
            }
            finally { Native.FPDFPage_CloseAnnot(a); }
        }
        return (IReadOnlyList<AnnotationInfo>)list;
    }, ct);

    /// <summary>Belgedeki tüm kullanıcı açıklamaları (sayfa sırasıyla).</summary>
    public Task<IReadOnlyList<AnnotationInfo>> GetAllAnnotationsAsync(CancellationToken ct = default) => AnnotationQueries.AllAsync(this, ct);

    /// <summary>Açıklamayı siler. Dizin değişmiş olabileceği için tür ve dikdörtgen eşleşmesi doğrulanır.</summary>
    public Task<bool> RemoveAnnotationAsync(AnnotationInfo info) => _engine.RunAsync(() =>
    {
        var page = Page(info.Page);
        int n = Native.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < n; i++)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            bool match;
            try
            {
                int st = Native.FPDFAnnot_GetSubtype(a);
                match = IsUserAnnot(st) && KindOf(st) == info.Kind && Native.FPDFAnnot_GetRect(a, out var r) != 0
                    && Math.Abs(Math.Min(r.left, r.right) - info.Rect.Left) < 0.5 && Math.Abs(Math.Max(r.top, r.bottom) - info.Rect.Top) < 0.5;
            }
            finally { Native.FPDFPage_CloseAnnot(a); }
            if (match && Native.FPDFPage_RemoveAnnot(page, i) != 0)
            {
                RemoveOrphanPopups(page);
                ClosePage(info.Page);
                MarkChanged(structure: false);
                return true;
            }
        }
        return false;
    });

    /// <summary>Sayfadaki kullanıcı açıklamalarını kaldırır (bağlantılar ve form alanları kalır). Kaldırılan sayısı.</summary>
    public Task<int> RemovePageAnnotationsAsync(int pageIndex) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        int removed = 0;
        for (int i = Native.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            int st;
            try { st = Native.FPDFAnnot_GetSubtype(a); } finally { Native.FPDFPage_CloseAnnot(a); }
            if ((IsUserAnnot(st) || st == Native.FPDF_ANNOT_POPUP) && Native.FPDFPage_RemoveAnnot(page, i) != 0 && st != Native.FPDF_ANNOT_POPUP) removed++;
        }
        if (removed > 0) { ClosePage(pageIndex); MarkChanged(structure: false); }
        return removed;
    });

    /// <summary>Sahibi kalmayan açılır not pencerelerini (Popup) temizler.</summary>
    private static void RemoveOrphanPopups(nint page)
    {
        bool hasNotes = false;
        int n = Native.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < n; i++)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            try { int st = Native.FPDFAnnot_GetSubtype(a); if (IsUserAnnot(st)) { hasNotes = true; break; } } finally { Native.FPDFPage_CloseAnnot(a); }
        }
        if (hasNotes) return;
        for (int i = n - 1; i >= 0; i--)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            int st;
            try { st = Native.FPDFAnnot_GetSubtype(a); } finally { Native.FPDFPage_CloseAnnot(a); }
            if (st == Native.FPDF_ANNOT_POPUP) Native.FPDFPage_RemoveAnnot(page, i);
        }
    }

    private static void SetColor(nint annot, uint rgb) =>
        Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);

    private static void SetString(nint annot, string key, string value)
    {
        var k = Encoding.ASCII.GetBytes(key + "\0");
        var v = (value + "\0").ToCharArray();
        fixed (byte* kp = k) fixed (char* vp = v) Native.FPDFAnnot_SetStringValue(annot, kp, (ushort*)vp);
    }

    private static string GetString(nint annot, string key)
    {
        var k = Encoding.ASCII.GetBytes(key + "\0");
        fixed (byte* kp = k)
        {
            ulong len = Native.FPDFAnnot_GetStringValue(annot, kp, null, new CULong(0)).Value;
            if (len <= 2) return "";
            var buf = new char[len / 2];
            fixed (char* bp = buf) Native.FPDFAnnot_GetStringValue(annot, kp, (ushort*)bp, new CULong((nuint)len));
            return new string(buf).TrimEnd('\0');
        }
    }

    private static string PdfDate(DateTime t)
    {
        var off = TimeZoneInfo.Local.GetUtcOffset(t);
        return $"D:{t:yyyyMMddHHmmss}{(off < TimeSpan.Zero ? '-' : '+')}{Math.Abs(off.Hours):00}'{Math.Abs(off.Minutes):00}'";
    }

    // ─────────────── Formlar ───────────────

    /// <summary>Kalıcı form ortamıyla işlem; formu olmayan belgede <paramref name="none"/> döner.</summary>
    private T WithForm<T>(Func<nint, Func<int, nint>, T> work, T none)
    {
        var h = Form();
        return h == 0 ? none : work(h, Page);
    }

    private static string ReadW(Func<nint, CULong, CULong> call)
    {
        ulong len = call(0, new CULong(0)).Value;
        if (len <= 2) return "";
        var buf = new char[len / 2];
        fixed (char* p = buf) call((nint)p, new CULong((nuint)len));
        return new string(buf).TrimEnd('\0');
    }

    private static FormFieldKind FieldKind(int t) => t switch
    {
        Native.FPDF_FORMFIELD_TEXTFIELD => FormFieldKind.Text,
        Native.FPDF_FORMFIELD_CHECKBOX => FormFieldKind.CheckBox,
        Native.FPDF_FORMFIELD_RADIOBUTTON => FormFieldKind.Radio,
        Native.FPDF_FORMFIELD_COMBOBOX => FormFieldKind.ComboBox,
        Native.FPDF_FORMFIELD_LISTBOX => FormFieldKind.ListBox,
        Native.FPDF_FORMFIELD_PUSHBUTTON => FormFieldKind.PushButton,
        Native.FPDF_FORMFIELD_SIGNATURE => FormFieldKind.Signature,
        _ => FormFieldKind.Unknown,
    };

    /// <summary>Belgedeki form alanları (sayfa ve konum sırasıyla).</summary>
    public Task<IReadOnlyList<FormFieldInfo>> GetFormFieldsAsync() => _engine.RunAsync(() => WithForm((h, getPage) =>
    {
        var list = new List<FormFieldInfo>();
        for (int pi = 0; pi < PageCount; pi++)
        {
            var page = getPage(pi);
            int n = Native.FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < n; i++)
            {
                var a = Native.FPDFPage_GetAnnot(page, i);
                if (a == 0) continue;
                try
                {
                    if (Native.FPDFAnnot_GetSubtype(a) != Native.FPDF_ANNOT_WIDGET) continue;
                    var kind = FieldKind(Native.FPDFAnnot_GetFormFieldType(h, a));
                    var name = ReadW((b, l) => Native.FPDFAnnot_GetFormFieldName(h, a, (ushort*)b, l));
                    var value = ReadW((b, l) => Native.FPDFAnnot_GetFormFieldValue(h, a, (ushort*)b, l));
                    var export = kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? ReadW((b, l) => Native.FPDFAnnot_GetFormFieldExportValue(h, a, (ushort*)b, l)) : "";
                    bool chk = kind is FormFieldKind.CheckBox or FormFieldKind.Radio && Native.FPDFAnnot_IsChecked(h, a) != 0;
                    var options = new List<string>(); int sel = -1;
                    if (kind is FormFieldKind.ComboBox or FormFieldKind.ListBox)
                    {
                        int oc = Native.FPDFAnnot_GetOptionCount(h, a);
                        for (int k = 0; k < oc; k++)
                        {
                            int kk = k;
                            options.Add(ReadW((b, l) => Native.FPDFAnnot_GetOptionLabel(h, a, kk, (ushort*)b, l)));
                            if (sel < 0 && Native.FPDFAnnot_IsOptionSelected(h, a, k) != 0) sel = k;
                        }
                        if (sel < 0 && value.Length > 0) sel = options.IndexOf(value);
                    }
                    bool ro = (Native.FPDFAnnot_GetFormFieldFlags(h, a) & 1) != 0;
                    Native.FPDFAnnot_GetRect(a, out var r);
                    list.Add(new FormFieldInfo(pi, i, name, kind, value, chk, export, options, sel, new RectF(r.left, r.bottom, r.right, r.top).Normalize(), ro));
                }
                finally { Native.FPDFPage_CloseAnnot(a); }
            }
        }
        return (IReadOnlyList<FormFieldInfo>)list;
    }, Array.Empty<FormFieldInfo>()));

    /// <summary>
    /// Form alanlarına değer yazar. PDFium'un form motoru alan değerini ve görünümünü (appearance) günceller; sonuç
    /// her PDF okuyucusunda aynı görünür. Yazılan alan sayısını döndürür.
    /// </summary>
    public Task<int> SetFormFieldsAsync(IReadOnlyList<FormFieldChange> changes) => _engine.RunAsync(() =>
    {
        int done = WithForm((h, getPage) =>
        {
            int count = 0;
            foreach (var c in changes)
            {
                var f = c.Field;
                if (f.ReadOnly) continue;
                var page = getPage(f.Page);
                var a = Native.FPDFPage_GetAnnot(page, f.AnnotIndex);
                if (a == 0) continue;
                try
                {
                    switch (f.Kind)
                    {
                        case FormFieldKind.Text when c.Text is not null:
                            if (Native.FORM_SetFocusedAnnot(h, a) == 0) continue;
                            Native.FORM_SelectAllText(h, page);
                            var t = (c.Text + "\0").ToCharArray();
                            fixed (char* tp = t) Native.FORM_ReplaceSelection(h, page, (ushort*)tp);
                            Native.FORM_ForceToKillFocus(h);
                            count++;
                            break;
                        case FormFieldKind.CheckBox or FormFieldKind.Radio when c.Checked is { } want:
                            if ((Native.FPDFAnnot_IsChecked(h, a) != 0) == want) break;
                            if (f.Kind == FormFieldKind.Radio && !want) break; // radyo düğmesi tıklanarak kapatılamaz; başka seçenek seçilir
                            double cx = (f.Rect.Left + f.Rect.Right) / 2, cy = (f.Rect.Top + f.Rect.Bottom) / 2;
                            Native.FORM_OnMouseMove(h, page, 0, cx, cy);
                            Native.FORM_OnLButtonDown(h, page, 0, cx, cy);
                            Native.FORM_OnLButtonUp(h, page, 0, cx, cy);
                            Native.FORM_ForceToKillFocus(h);
                            if ((Native.FPDFAnnot_IsChecked(h, a) != 0) == want) count++;
                            break;
                        case FormFieldKind.ComboBox or FormFieldKind.ListBox when c.Option is { } opt:
                            if (Native.FORM_SetFocusedAnnot(h, a) == 0) continue;
                            if (opt >= 0 && Native.FORM_SetIndexSelected(h, page, opt, 1) != 0) count++;
                            else if (opt < 0 && f.SelectedOption >= 0 && Native.FORM_SetIndexSelected(h, page, f.SelectedOption, 0) != 0) count++;
                            Native.FORM_ForceToKillFocus(h);
                            break;
                    }
                }
                finally { Native.FPDFPage_CloseAnnot(a); }
            }
            return count;
        }, 0);
        if (done > 0) { ClosePages(); MarkChanged(structure: false); }
        return done;
    });

    /// <summary>
    /// Açıklamaları ve form alanlarını sayfa içeriğine gömer (düzleştirme). Sonrasında değiştirilemezler; geri almak için
    /// Geri Al kullanılır. Düzleştirilen sayfa sayısı.
    /// </summary>
    public Task<int> FlattenAsync(IEnumerable<int>? pages = null) => _engine.RunAsync(() =>
    {
        ExitForm(); // düzleştirilen alanlar form ortamında kalmasın
        int flattened = 0;
        foreach (var i in (pages ?? Enumerable.Range(0, PageCount)).ToList())
        {
            var p = Native.FPDF_LoadPage(_doc, i);
            if (p == 0) continue;
            try { if (Native.FPDFPage_Flatten(p, Native.FLAT_NORMALDISPLAY) == Native.FLATTEN_SUCCESS) flattened++; }
            finally { Native.FPDF_ClosePage(p); }
        }
        if (flattened > 0) MarkChanged(structure: false);
        return flattened;
    });
}

internal static class AnnotationQueries
{
    public static async Task<IReadOnlyList<AnnotationInfo>> AllAsync(PdfDocument doc, CancellationToken ct)
    {
        var all = new List<AnnotationInfo>();
        for (int p = 0; p < doc.PageCount; p++) { ct.ThrowIfCancellationRequested(); all.AddRange(await doc.GetAnnotationsAsync(p, ct)); }
        return all;
    }
}
