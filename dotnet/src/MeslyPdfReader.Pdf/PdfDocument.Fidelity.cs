using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>
/// Sayfa içeriği yeniden yazılırken özgün görünümün korunması.
/// PDFium'un içerik üreticisi metin durumundaki sözcük aralığını (Tw) ve karakter aralığını (Tc) yazmaz: iki yana
/// yaslı ya da harf aralığı ayarlı metinler, sayfadaki başka bir şey düzenlendiğinde daralıp kayar. Bunu önlemek için
/// sayfa yazılmadan hemen önce her metin nesnesinin karakterlerinin gerçek (ekranda görünen) konumları ölçülüp
/// nesneye işlenir (FPDFText_SetPositions); üretici bunları TJ konum ayarları olarak yazar ve görünüm birebir kalır.
/// </summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>Son içerik yazımında konumu düzeltilen / düzeltilemeyen metin nesnesi sayısı (tanılama ve testler için).</summary>
    internal (int Fixed, int Failed) LastBake { get; private set; }

    private sealed record ObjGlyphs(string Text, List<(double X, double Y)> Origins);

    /// <summary>
    /// Metin aralıklarını koruyarak sayfa içeriğini yeniden üretir. Tüm düzenleme yolları bunu kullanır.
    /// 1) Her metin nesnesinin karakterlerinin ekrandaki gerçek kökenleri kaydedilir. 2) Sayfa yazılır ve yeniden
    /// yüklenir. 3) Kökeni değişen nesnelere (Tw/Tc kaybı) gerçek konumlar işlenir ve sayfa bir kez daha yazılır —
    /// bu kez nesnelerde gizli aralık değeri kalmadığından TJ ayarları doğru yazılır.
    /// </summary>
    internal void GenerateContent(int pageIndex, nint page)
    {
        var before = RecordGlyphs(page);
        if (Native.FPDFPage_GenerateContent(page) == 0) throw new PdfException(PdfErrorKind.Unknown, "Sayfa içeriği yazılamadı.");
        if (before.Count == 0) { LastBake = (0, 0); return; }
        ClosePage(pageIndex);
        var p2 = Page(pageIndex);
        var after = RecordGlyphs(p2);
        int fixedN = 0, failed = 0;
        foreach (var (index, want) in before)
        {
            if (!after.TryGetValue(index, out var got) || got.Text != want.Text || got.Origins.Count != want.Origins.Count) continue;
            double maxD = 0;
            for (int k = 0; k < want.Origins.Count; k++)
                maxD = Math.Max(maxD, Math.Abs(want.Origins[k].X - got.Origins[k].X) + Math.Abs(want.Origins[k].Y - got.Origins[k].Y));
            if (maxD < 0.02) continue;
            var obj = Native.FPDFPage_GetObject(p2, index);
            if (obj != 0 && ApplyOrigins(obj, want.Origins)) fixedN++; else failed++;
        }
        if (fixedN > 0 && Native.FPDFPage_GenerateContent(p2) == 0) throw new PdfException(PdfErrorKind.Unknown, "Sayfa içeriği yazılamadı.");
        LastBake = (fixedN, failed);
    }

    /// <summary>Sayfa nesne sırası → (metin, karakter kökenleri). Bağlaç karakterleri aynı kökeni paylaşır, tek sayılır.</summary>
    private static Dictionary<int, ObjGlyphs> RecordGlyphs(nint page)
    {
        var result = new Dictionary<int, ObjGlyphs>();
        var tp = Native.FPDFText_LoadPage(page);
        if (tp == 0) return result;
        try
        {
            var indexOf = new Dictionary<nint, int>();
            int count = Native.FPDFPage_CountObjects(page);
            for (int i = 0; i < count; i++) { var o = Native.FPDFPage_GetObject(page, i); if (o != 0) indexOf[o] = i; }
            var texts = new Dictionary<int, System.Text.StringBuilder>();
            int n = Native.FPDFText_CountChars(tp);
            for (int i = 0; i < n; i++)
            {
                if (Native.FPDFText_IsGenerated(tp, i) != 0) continue;
                var obj = Native.FPDFText_GetTextObject(tp, i);
                if (obj == 0 || !indexOf.TryGetValue(obj, out var idx)) continue;
                if (Native.FPDFText_GetCharOrigin(tp, i, out var x, out var y) == 0) continue;
                if (!result.TryGetValue(idx, out var g)) { g = new ObjGlyphs("", new()); result[idx] = g; texts[idx] = new(); }
                texts[idx].Append((char)Native.FPDFText_GetUnicode(tp, i));
                if (g.Origins.Count > 0 && Math.Abs(g.Origins[^1].X - x) < 1e-4 && Math.Abs(g.Origins[^1].Y - y) < 1e-4) continue;
                g.Origins.Add((x, y));
            }
            foreach (var k in result.Keys.ToList()) result[k] = result[k] with { Text = texts[k].ToString() };
            foreach (var k in result.Where(kv => kv.Value.Origins.Count < 2).Select(kv => kv.Key).ToList()) result.Remove(k);
        }
        finally { Native.FPDFText_ClosePage(tp); }
        return result;
    }

    /// <summary>Nesnenin karakterlerini verilen sayfa kökenlerine yerleştirir.</summary>
    private static bool ApplyOrigins(nint obj, List<(double X, double Y)> pts)
    {
        if (Native.FPDFPageObj_GetMatrix(obj, out var m) == 0) return false;
        double scale = Math.Sqrt(m.a * m.a + m.b * m.b);
        if (scale < 1e-6) return false;
        double ux = m.a / scale, uy = m.b / scale;
        var pos = new float[pts.Count - 1];
        for (int k = 1; k < pts.Count; k++)
        {
            double dx = pts[k].X - pts[0].X, dy = pts[k].Y - pts[0].Y;
            double along = dx * ux + dy * uy, across = -dx * uy + dy * ux;
            if (Math.Abs(across) > 0.5 && Math.Abs(across) > Math.Abs(along)) return false; // dikey yazım
            pos[k - 1] = (float)(along / scale);
        }
        bool ok;
        fixed (float* p = pos) ok = Native.FPDFText_SetPositions(obj, p, (nuint)pos.Length) != 0;
        if (!ok) return false;
        double ox = pts[0].X - m.e, oy = pts[0].Y - m.f;
        if (Math.Abs(ox) > 1e-3 || Math.Abs(oy) > 1e-3) Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, ox, oy);
        return true;
    }
}
