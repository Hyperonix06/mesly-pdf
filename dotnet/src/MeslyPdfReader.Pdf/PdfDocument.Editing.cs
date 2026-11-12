using System.Runtime.InteropServices;
using System.Text;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>
/// Aşama 3 — gerçek içerik düzenleme. Tüm işlemler sayfanın içerik nesneleri üzerinde yapılır
/// (nesne silinir / taşınır / yeniden oluşturulur); hiçbir değişiklik üstüne kutu çizerek taklit edilmez.
/// Her işlem sonunda sayfa içeriği yeniden üretilir (FPDFPage_GenerateContent).
/// </summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>Mesly ile yazılmış paragraf metni; "box" parametresinde paragraf kutusu saklanır.</summary>
    public const string MarkText = "MeslyText";
    public const string MarkWatermark = "MeslyWatermark", MarkHeaderFooter = "MeslyHeaderFooter",
        MarkBackground = "MeslyBackground", MarkUnderline = "MeslyUnderline", MarkArtifact = "Artifact";

    // ─────────────── Nesneleri okuma ───────────────

    public Task<IReadOnlyList<PageObjectInfo>> GetPageObjectsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        int n = Native.FPDFPage_CountObjects(page);
        var list = new List<PageObjectInfo>(n);
        for (int i = 0; i < n; i++)
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0) continue;
            list.Add(Describe(o, i, tp));
        }
        return (IReadOnlyList<PageObjectInfo>)list;
    }, ct);

    private PageObjectInfo Describe(nint o, int i, nint textPage)
    {
        var kind = (PageObjectKind)Native.FPDFPageObj_GetType(o);
        Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
        var bounds = new RectF(l, b, r, t).Normalize();
        uint fill = Native.FPDFPageObj_GetFillColor(o, out var fr, out var fg, out var fb, out _) != 0 ? (fr << 16) | (fg << 8) | fb : 0;
        uint stroke = Native.FPDFPageObj_GetStrokeColor(o, out var sr, out var sg, out var sb, out _) != 0 ? (sr << 16) | (sg << 8) | sb : 0;
        string? role = RoleOf(o);
        if (kind != PageObjectKind.Text) return new PageObjectInfo(i, kind, bounds, FillRgb: fill, StrokeRgb: stroke, MeslyRole: role);
        var text = ReadObjText(o, textPage);
        var (font, size, bold, italic) = FontInfo(o);
        bool invisible = Native.FPDFTextObj_GetTextRenderMode(o) == 3;
        return new PageObjectInfo(i, kind, bounds, text, font, size, fill, stroke, bold, italic, invisible, role);
    }

    private static string ReadObjText(nint o, nint textPage)
    {
        uint len = (uint)Native.FPDFTextObj_GetText(o, textPage, null, new CULong(0u)).Value;
        if (len <= 2) return "";
        var buf = new ushort[len / 2 + 1];
        fixed (ushort* p = buf) Native.FPDFTextObj_GetText(o, textPage, p, new CULong(len));
        var s = new string(buf.Select(c => (char)c).ToArray());
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Replace('￾', '-').Replace('\u0002', '-');
    }

    private static (string? Name, double Size, bool Bold, bool Italic) FontInfo(nint o)
    {
        var f = Native.FPDFTextObj_GetFont(o);
        string? name = null;
        if (f != 0)
        {
            nuint len = Native.FPDFFont_GetBaseFontName(f, null, 0);
            if (len > 1)
            {
                var buf = new byte[(int)len];
                fixed (byte* p = buf) Native.FPDFFont_GetBaseFontName(f, p, len);
                name = Encoding.UTF8.GetString(buf, 0, (int)len - 1);
            }
        }
        Native.FPDFTextObj_GetFontSize(o, out var size);
        double scale = 1;
        if (Native.FPDFPageObj_GetMatrix(o, out var m) != 0) scale = Math.Sqrt(m.c * m.c + m.d * m.d);
        var (_, nb, ni) = TextLayout.ParseFontName(name);
        bool bold = nb, italic = ni;
        if (f != 0)
        {
            if (Native.FPDFFont_GetWeight(f) >= 600) bold = true;
            if ((Native.FPDFFont_GetFlags(f) & 64) != 0) italic = true;
        }
        return (name, Math.Round(size * (scale > 0 ? scale : 1), 2), bold, italic);
    }

    private static string? RoleOf(nint o)
    {
        int n = Native.FPDFPageObj_CountMarks(o);
        for (int k = 0; k < n; k++)
        {
            var mark = Native.FPDFPageObj_GetMark(o, new CULong((uint)k));
            if (mark == 0) continue;
            var buf = stackalloc ushort[64];
            if (Native.FPDFPageObjMark_GetName(mark, buf, new CULong(128u), out var outLen) == 0) continue;
            int chars = Math.Max(0, (int)outLen.Value / 2 - 1);
            var name = new string((char*)buf, 0, Math.Min(chars, 63));
            if (name.StartsWith("Mesly", StringComparison.Ordinal)) return name;
        }
        return null;
    }

    /// <summary>Sayfadaki metinleri paragraflara gruplar (satırlar → paragraflar).</summary>
    public Task<IReadOnlyList<TextParagraph>> GetParagraphsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        int n = Native.FPDFPage_CountObjects(page);
        var items = new List<TextItem>();
        for (int i = 0; i < n; i++)
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0 || Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_TEXT) continue;
            if (RoleOf(o) is MarkWatermark or MarkHeaderFooter) continue;
            var text = ReadObjText(o, tp);
            if (text.Length == 0) continue;
            Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
            Native.FPDFPageObj_GetMatrix(o, out var m);
            var (font, size, bold, italic) = FontInfo(o);
            Native.FPDFPageObj_GetFillColor(o, out var fr, out var fg, out var fb, out _);
            bool rotated = Math.Abs(m.b) > 0.01 * Math.Abs(m.a) || Math.Abs(m.c) > 0.01 * Math.Abs(m.d) || m.a < 0;
            double angle = rotated ? Math.Atan2(m.b, m.a) * 180 / Math.PI : 0;
            items.Add(new TextItem(i, new RectF(l, b, r, t).Normalize(), text, font, size > 0 ? size : 10, (fr << 16) | (fg << 8) | fb, bold, italic, m.f, m.e, rotated, angle, BlockTag(o)));
        }
        // Sayfanın görünen kutusu (kırpma kutusu; yoksa ortam kutusu): ortalı / sağa yaslı tek satırları anlamak için.
        float pl, pr;
        if (Native.FPDFPage_GetCropBox(page, out pl, out _, out pr, out _) == 0 && Native.FPDFPage_GetMediaBox(page, out pl, out _, out pr, out _) == 0) { pl = 0; pr = 0; }
        return (IReadOnlyList<TextParagraph>)BuildParagraphs(items, Math.Min(pl, pr), Math.Max(pl, pr));
    }, ct);

    internal sealed record TextItem(int Index, RectF Box, string Text, string? Font, double Size, uint Rgb, bool Bold, bool Italic, double Baseline, double X, bool Rotated, double Angle = 0, string? Tag = null);

    private static readonly byte[] BoxKey = Encoding.ASCII.GetBytes("box\0");

    /// <summary>Nesnedeki MeslyText işaretinin "box" değeri.</summary>
    private static string? BlockTag(nint o)
    {
        int n = Native.FPDFPageObj_CountMarks(o);
        for (int k = 0; k < n; k++)
        {
            var mark = Native.FPDFPageObj_GetMark(o, new CULong((uint)k));
            if (mark == 0) continue;
            var nb = stackalloc ushort[32];
            if (Native.FPDFPageObjMark_GetName(mark, nb, new CULong(64u), out var nl) == 0) continue;
            if (new string((char*)nb, 0, Math.Max(0, Math.Min(31, (int)nl.Value / 2 - 1))) != MarkText) continue;
            var buf = stackalloc ushort[128];
            fixed (byte* key = BoxKey)
                if (Native.FPDFPageObjMark_GetParamStringValue(mark, key, buf, new CULong(256u), out var len) == 0) return null;
                else return new string((char*)buf, 0, Math.Max(0, Math.Min(127, (int)len.Value / 2 - 1)));
        }
        return null;
    }

    internal static string FormatBlock(TextBlockInfo b) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{(int)b.Align};{b.Left:0.###};{b.Width:0.###};{b.FirstIndent:0.###}");

    internal static TextBlockInfo? ParseBlock(string? tag)
    {
        if (tag is null) return null;
        var p = tag.Split(';');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (p.Length != 4 || !int.TryParse(p[0], out var a) || a is < 0 or > 3) return null;
        if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out var l) || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out var w)
            || !double.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out var ind) || w <= 0) return null;
        return new TextBlockInfo((TextAlign)a, l, w, ind);
    }

    internal static List<TextParagraph> BuildParagraphs(List<TextItem> items, double pageLeft = 0, double pageRight = 0)
    {
        var result = new List<TextParagraph>();
        // 1) Satırlar
        var lines = new List<List<TextItem>>();
        foreach (var it in items.Where(i => !i.Rotated).OrderByDescending(i => Math.Round(i.Baseline, 1)).ThenBy(i => i.Box.Left))
        {
            var line = lines.FirstOrDefault(L =>
            {
                var last = L[^1];
                return Math.Abs(last.Baseline - it.Baseline) <= 0.3 * Math.Max(last.Size, it.Size)
                    && Math.Abs(last.Size - it.Size) <= 0.15 * last.Size
                    && it.Box.Left - L.Max(x => x.Box.Right) < 2.5 * it.Size
                    && it.Box.Left >= L[0].Box.Left - it.Size;
            });
            if (line is null) lines.Add(new List<TextItem> { it }); else line.Add(it);
        }
        foreach (var L in lines) L.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
        lines = lines.OrderByDescending(L => L[0].Baseline).ThenBy(L => L[0].Box.Left).ToList();

        // Sayfanın metin alanı (tek satırlık paragrafların hizasını anlamak için)
        double lm = lines.Count > 0 ? lines.Min(L => L[0].Box.Left) : 0;
        double rm = lines.Count > 0 ? lines.Max(L => L.Max(x => x.Box.Right)) : 0;
        // Ortak sağ kenar: en az 3 satırın (≤1 pt içinde) paylaştığı en sağdaki sağ kenar — iki yana yaslı sütunun kenarı.
        var rightsAll = lines.Select(L => L.Max(x => x.Box.Right)).OrderBy(r => r).ToList();
        double colEdge = 0;
        for (int i = 0; i < rightsAll.Count; i++)
            if (rightsAll.Count(r => Math.Abs(r - rightsAll[i]) <= 1.0) >= 3) colEdge = Math.Max(colEdge, rightsAll[i]);
        // Sayfa kenarları tek bir aşırı satıra (ör. taşan bir satır) değil, en az 3 satırın paylaştığı kenarlara dayanır.
        var leftsAll = lines.Select(L => L[0].Box.Left).ToList();
        var sharedLefts = leftsAll.Where(l => leftsAll.Count(o => Math.Abs(o - l) <= 1.0) >= 3).ToList();
        if (sharedLefts.Count > 0) lm = sharedLefts.Min();
        if (colEdge > 0) rm = colEdge;

        // 2) Paragraflar
        var used = new bool[lines.Count];
        var groups = new List<(List<List<TextItem>> Group, double Step)>();
        for (int s = 0; s < lines.Count; s++)
        {
            if (used[s]) continue;
            var group = new List<List<TextItem>> { lines[s] };
            used[s] = true;
            double step = -1;
            for (int k = s + 1; k < lines.Count; k++)
            {
                if (used[k]) continue;
                var prev = group[^1]; var cur = lines[k];
                double size = prev[0].Size;
                double dy = prev[0].Baseline - cur[0].Baseline;
                if (dy <= 0) continue;
                if (dy > 1.9 * size) break;
                bool sameStyle = Math.Abs(cur[0].Size - size) <= 0.15 * size && cur[0].Font == prev[0].Font;
                double pl = prev[0].Box.Left, cl = cur[0].Box.Left, pr = prev.Max(x => x.Box.Right), cr = cur.Max(x => x.Box.Right);
                bool overlapH = Math.Min(pr, cr) - Math.Max(pl, cl) > 0;
                bool consistent = step < 0 || Math.Abs(dy - step) <= 0.35 * step;
                if (!sameStyle || !overlapH || !consistent || dy < 0.8 * size) continue;
                // Önceki satır kısa kaldıysa paragraf orada biter: cümle sonuyla bitiyor ya da sonraki satırın ilk
                // sözcüğü önceki satıra sığacakken alta geçmiş (boş satırsız ardışık paragraflar).
                double colRight = Math.Max(group.Max(L => L.Max(x => x.Box.Right)), cr);
                double free = colRight - pr;
                if (free > 2 * size)
                {
                    var prevText = string.Concat(prev.Select(x => x.Text)).TrimEnd();
                    var firstWord = string.Concat(cur.Select(x => x.Text)).TrimStart().Split(' ')[0];
                    bool sentenceEnd = prevText.Length > 0 && ".:!?…\"”»)".Contains(prevText[^1]);
                    if (sentenceEnd || firstWord.Length * 0.55 * size < free - size) break;
                }
                group.Add(cur); used[k] = true; step = dy;
            }
            groups.Add((group, step));
        }
        var horizontal = items.Where(i => !i.Rotated).ToList();
        foreach (var (group, step) in groups)
            result.Add(MakeParagraph(group, step, lm, rm, colEdge, RightLimitOf(group, horizontal), pageLeft, pageRight));
        // 3) Döndürülmüş metin: her nesne kendi paragrafı; açısı ve kökeni korunur
        foreach (var it in items.Where(i => i.Rotated))
            result.Add(new TextParagraph(new[] { it.Index }, it.Box, it.Text, it.Font, it.Size, it.Rgb, it.Bold, it.Italic, it.Baseline, it.Size * 1.2, TextAlign.Left,
                new[] { new TextLineInfo(it.Text, it.X, it.Baseline, it.Box.Right) }, 0, it.Angle));
        return result;
    }

    /// <summary>
    /// Paragrafın sağa doğru büyüyebileceği sınır: aynı satır hizasında sağdaki ilk başka metnin (ör. yandaki tablo
    /// hücresi, sağ sütun) biraz solu. Yoksa 0 (sınır yok).
    /// </summary>
    private static double RightLimitOf(List<List<TextItem>> group, List<TextItem> all)
    {
        var mine = group.SelectMany(x => x).Select(x => x.Index).ToHashSet();
        double limit = double.MaxValue;
        foreach (var line in group)
        {
            double right = line.Max(x => x.Box.Right), size = line[0].Size;
            double top = line.Max(x => x.Box.Top), bottom = line.Min(x => x.Box.Bottom);
            foreach (var o in all)
            {
                if (mine.Contains(o.Index) || o.Box.Left < right - 0.5) continue;
                if (o.Box.Top <= bottom || o.Box.Bottom >= top) continue;
                limit = Math.Min(limit, o.Box.Left - Math.Max(2, 0.3 * size));
            }
        }
        return limit == double.MaxValue ? 0 : limit;
    }

    private static TextParagraph MakeParagraph(List<List<TextItem>> group, double step, double lm, double rm, double colEdge, double rightLimit, double pageLeft = 0, double pageRight = 0)
    {
        var sb = new StringBuilder();
        var infos = new List<TextLineInfo>();
        foreach (var line in group)
        {
            var ls = new StringBuilder();
            TextItem? prev = null;
            foreach (var it in line)
            {
                if (prev is not null && ls.Length > 0 && !ls.ToString().EndsWith(' ') && !it.Text.StartsWith(' ') && it.Box.Left - prev.Box.Right > 0.15 * it.Size)
                    ls.Append(' ');
                ls.Append(it.Text);
                prev = it;
            }
            var lineText = ls.ToString().Trim();
            infos.Add(new TextLineInfo(lineText, line[0].X, line[0].Baseline, line.Max(x => x.Box.Right)));
            if (sb.Length > 0)
            {
                var cur = sb.ToString();
                if (cur.EndsWith('-') && lineText.Length > 0 && char.IsLower(lineText[0])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(lineText);
        }
        var all = group.SelectMany(x => x).ToList();
        var bounds = all.Select(i => i.Box).Aggregate((a, b) => a.Union(b));
        var first = group[0][0];
        // Mesly ile yazılmış paragraf: tüm nesneler aynı kutuyu taşıyorsa hizalama tahmin edilmez, kayıttan okunur.
        var tags = all.Select(i => i.Tag).Distinct().ToList();
        var block = tags.Count == 1 ? ParseBlock(tags[0]) : null;
        var align = block?.Align ?? DetectAlign(group, lm, rm, colEdge, pageLeft, pageRight);
        var bold = all.Count(i => i.Bold) * 2 > all.Count;
        var italic = all.Count(i => i.Italic) * 2 > all.Count;
        return new TextParagraph(all.Select(i => i.Index).OrderBy(i => i).ToArray(), bounds, sb.ToString(), first.Font, first.Size, first.Rgb,
            bold, italic, first.Baseline, step > 0 ? step : first.Size * 1.2, align, infos, rightLimit, 0, block);
    }

    /// <summary>
    /// Hizalama: çok satırda sol/sağ/orta kenarların tutarlılığı (ilk satır girintisi ve son satır hesaba katılır);
    /// tek satırda sayfanın metin alanına göre konum (ortalanmış başlık, sağa yaslı tarih).
    /// </summary>
    internal static TextAlign DetectAlign(List<List<TextItem>> group, double lm, double rm, double colEdge = 0, double pageLeft = 0, double pageRight = 0)
    {
        var first = group[0][0];
        double size = first.Size;
        var lefts = group.Select(L => L[0].Box.Left).ToList();
        var rights = group.Select(L => L.Max(x => x.Box.Right)).ToList();
        if (group.Count == 1)
        {
            double l = lefts[0], r = rights[0], c = (l + r) / 2, mid = (lm + rm) / 2, wArea = rm - lm;
            if (wArea <= 0 || l - lm < 1.5 * size) return TextAlign.Left;
            double tolC = Math.Max(2, 0.012 * wArea);
            bool page = pageRight - pageLeft > 0;
            // Ortalı: metin alanının ortasında ya da (kenar boşlukları eşitse) sayfanın ortasında.
            if (Math.Abs(c - mid) <= tolC || page && Math.Abs(c - (pageLeft + pageRight) / 2) <= tolC) return TextAlign.Center;
            // Sağa yaslı: metin alanının sağ kenarında ya da sol boşluğun sayfanın sağındaki simetriğinde.
            if (Math.Abs(r - rm) <= 1.5 || page && Math.Abs(r - (pageRight - (lm - pageLeft))) <= 1.5) return TextAlign.Right;
            return TextAlign.Left;
        }
        double tol = size * 0.6;
        var restLefts = lefts.Skip(1).ToList();
        bool leftOk = restLefts.Max() - restLefts.Min() < tol && lefts[0] >= restLefts.Min() - tol; // ilk satır girintili olabilir
        bool rightAll = rights.Max() - rights.Min() < tol;
        var centers = lefts.Zip(rights, (a, b) => (a + b) / 2).ToList();
        bool centerOk = centers.Max() - centers.Min() < tol;
        if (leftOk)
        {
            // İki yana yaslı: son satır dışındaki tüm satırların sağ kenarı aynı (≤1 pt) ve son satır daha kısa ya da 3+ satır.
            var body = rights.Take(rights.Count - 1).ToList();
            bool flushRight = body.Max() - body.Min() <= 1.0;
            // İki satırlık paragrafta tek kanıt, ilk satırın sayfadaki ortak sütun kenarına (≥3 satırın paylaştığı) denk gelmesi.
            bool fullWidth = colEdge > 0 && Math.Abs(body.Max() - colEdge) <= 1.0;
            if (flushRight && (group.Count >= 3 || fullWidth) && rights[^1] < body.Max() - size) return TextAlign.Justify;
            if (flushRight && group.Count >= 3 && rightAll) return TextAlign.Justify;
            return TextAlign.Left;
        }
        if (centerOk) return TextAlign.Center;
        if (rightAll) return TextAlign.Right;
        return TextAlign.Left;
    }

    // ─────────────── Metin oluşturma / değiştirme ───────────────

    /// <summary>Verilen yazı tipiyle metnin genişliği (pt). PDFium ile ölçülür.</summary>
    public Task<double> MeasureTextAsync(string text, byte[] font, double size) => _engine.RunAsync(() => Measure(text, LoadFont(font), size));

    private double Measure(string text, nint font, double size)
    {
        if (text.Length == 0) return 0;
        var o = Native.FPDFPageObj_CreateTextObj(_doc, font, (float)size);
        try
        {
            fixed (char* t = text + "\0") Native.FPDFText_SetText(o, (ushort*)t);
            Native.FPDFPageObj_GetBounds(o, out var l, out _, out var r, out _);
            // Sondaki boşluk sınırlara girmez: yaklaşık genişlik ekle.
            double trailing = text.Length - text.TrimEnd().Length;
            return Math.Max(0, r - Math.Min(0, l)) + trailing * size * 0.28;
        }
        finally { Native.FPDFPageObj_Destroy(o); }
    }

    /// <summary>
    /// Paragrafı değiştirir: eski metin nesneleri sayfadan gerçekten silinir, yeni metin özgün satırların başlangıç
    /// noktasından, özgün taban çizgisinden ve hizalamasıyla yeniden akıtılır. Genişlik özgün satırların hem özgün hem
    /// yeni yazı tipindeki genişliğinden hesaplanır: metin aynıysa satırlar aynı yerden kırılır, tek satırlık metin
    /// (başlık, tablo hücresi) alt satıra düşmez. Döndürülen dikdörtgen yeni metnin kapladığı alandır.
    /// </summary>
    public Task<RectF> ReplaceParagraphAsync(int pageIndex, TextParagraph paragraph, string newText, TextStyle style, double? width = null) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        // Silinecek nesneleri, indeksleri kaymadan önce tutamak olarak topla.
        var targets = paragraph.ObjectIndices.Select(i => Native.FPDFPage_GetObject(page, i)).Where(o => o != 0).ToList();
        // Taranmış sayfa: metin görünmez OCR katmanıysa, eski yazı görüntünün içindedir → görüntüdeki pikseller de silinir.
        bool ocrText = targets.Count > 0 && targets.All(o => Native.FPDFPageObj_GetType(o) == Native.FPDF_PAGEOBJ_TEXT && Native.FPDFTextObj_GetTextRenderMode(o) == 3);
        foreach (var o in AllObjects(page).Where(o => RoleOf(o) == MarkUnderline && Intersects(Bounds(o), paragraph.Bounds))) targets.Add(o);
        targets = targets.Distinct().ToList();

        double lh = style.LineSpacing > 0 ? style.Size * style.LineSpacing : paragraph.LineHeight;
        RectF result = default;
        bool placed = false;
        // 1) Özgün gömülü yazı tipi (eski nesneler silinmeden önce: yazı tipi onlar tarafından canlı tutulur).
        if (style.UseOriginalFont && !ocrText && OriginalFont(targets) is { } orig)
        {
            var created = new List<(nint Obj, string Text)>();
            double M(string t) => Measure(t, orig, style.Size);
            var plan = PlanParagraph(paragraph, newText, style, width, M);
            try { result = LayoutText(page, newText, style, plan.Left, plan.Baseline, plan.Width, lh, null, paragraph.Angle, plan.FirstIndent, orig, created, paragraph.Lines, tagBlock: true); }
            catch { }
            if (created.Count > 0 && VerifyGlyphs(page, created)) placed = true;
            else
                foreach (var (o, _) in created)
                    if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
            LastUsedOriginalFont = placed;
        }
        else LastUsedOriginalFont = false;
        // 2) Seçilen / eşleştirilen sistem yazı tipi.
        if (!placed)
        {
            var font = LoadFont(style.FontData);
            double M(string t) => Measure(t, font, style.Size);
            var plan = PlanParagraph(paragraph, newText, style, width, M);
            result = LayoutText(page, newText, style, plan.Left, plan.Baseline, plan.Width, lh, null, paragraph.Angle, plan.FirstIndent, font, null, paragraph.Lines, tagBlock: true);
        }
        if (ocrText) EraseImageUnder(page, paragraph.Bounds);
        foreach (var o in targets)
            if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
        Commit(pageIndex, page);
        return result;
    });

    /// <summary>Son paragraf değişiminde özgün gömülü yazı tipinin kullanılıp kullanılmadığı (durum iletisi ve testler için).</summary>
    public bool LastUsedOriginalFont { get; private set; }

    /// <summary>Paragrafın tüm metin nesneleri aynı yazı tipini kullanıyorsa o yazı tipi.</summary>
    private static nint? OriginalFont(List<nint> objs)
    {
        nint font = 0;
        foreach (var o in objs)
        {
            if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_TEXT) continue;
            var f = Native.FPDFTextObj_GetFont(o);
            if (f == 0) return null;
            if (font == 0) font = f; else if (font != f) return null;
        }
        return font == 0 ? null : font;
    }

    /// <summary>Oluşturulan nesnelerin metni, istenen metinle aynı okunuyor mu (yazı tipinde eksik karakter yok mu).</summary>
    private static bool VerifyGlyphs(nint page, List<(nint Obj, string Text)> created)
    {
        var tp = Native.FPDFText_LoadPage(page);
        if (tp == 0) return false;
        try
        {
            foreach (var (o, text) in created)
            {
                var got = new string(ReadObjText(o, tp).Where(c => !char.IsWhiteSpace(c)).ToArray());
                var want = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (got != want) return false;
                var b = Bounds(o);
                if (want.Length > 0 && b.Width < 0.05) return false;
            }
            return true;
        }
        finally { Native.FPDFText_ClosePage(tp); }
    }

    /// <summary>Yeni metnin yerleşimi: sütunun sol kenarı (köken), ilk taban çizgisi, satır genişliği ve ilk satır girintisi.</summary>
    internal readonly record struct ParagraphPlan(double Left, double Baseline, double Width, double FirstIndent);

    internal static ParagraphPlan PlanParagraph(TextParagraph p, string newText, TextStyle style, double? width, Func<string, double> measure)
    {
        const double Slack = 0.75; // ölçüm yuvarlaması için pay (pt)
        var L = p.Lines;
        if (L is null || L.Count == 0)
            return new(p.Bounds.Left, p.FirstBaseline, width ?? Math.Max(p.Bounds.Width, style.Size * 4), 0);
        double restX = L.Count > 1 ? L.Skip(1).Min(l => l.X) : L[0].X;
        double indent = L.Count > 1 && L[0].X - restX > 0.5 ? L[0].X - restX : 0;
        string flat = newText.Replace("\r\n", " ").Replace('\n', ' ');

        // Döndürülmüş metin: kökeninden, kendi açısıyla; satır kırılmaz.
        if (Math.Abs(p.Angle) > 0.01)
            return new(L[0].X, L[0].Baseline, width ?? Math.Max(measure(flat), L[0].Right - L[0].X) + Slack, 0);

        // Kullanıcı genişliği değiştirdi (yeniden boyutlandırma): yeni kutunun solundan, girinti korunarak.
        if (width is { } w)
            return new(p.Bounds.Left + (L[0].X - p.Bounds.Left) - indent, L[0].Baseline, Math.Max(w, style.Size * 2), indent);

        // Daha önce Mesly ile yazılmış paragraf: kayıtlı kutu birebir kullanılır (tek satır büyüyebilir).
        if (p.Block is { } bk)
        {
            double needB = measure(flat) + Slack;
            // Mevcut satırlar yeni yazı tipinde de sığsın: metin aynıysa satırlar aynı yerden kırılır.
            double fitW = L.Max(l => measure(l.Text) + (l.X - restX)) + Slack;
            if (L.Count > 1 || flat.Contains('\n')) return new(bk.Left, L[0].Baseline, Math.Max(bk.Width, fitW), bk.FirstIndent);
            switch (style.Align)
            {
                case TextAlign.Center: { double wB = Math.Max(bk.Width, needB); return new(bk.Left + bk.Width / 2 - wB / 2, L[0].Baseline, wB, 0); }
                case TextAlign.Right: { double wB = Math.Max(bk.Width, needB); return new(bk.Left + bk.Width - wB, L[0].Baseline, wB, 0); }
                default:
                {
                    double room = p.RightLimit > 0 ? p.RightLimit - bk.Left : double.MaxValue;
                    return new(bk.Left, L[0].Baseline, Math.Max(bk.Width, Math.Min(needB, room)), bk.FirstIndent);
                }
            }
        }

        // Özgün satırların genişliği: hem özgün mürekkep genişliği hem yeni yazı tipinde ölçülen genişlik.
        double colW = 0;
        foreach (var l in L)
            colW = Math.Max(colW, Math.Max(l.Right - restX, measure(l.Text) + (l.X - restX)));
        colW += Slack;

        if (L.Count == 1)
        {
            var l = L[0];
            double need = measure(flat) + Slack;
            switch (style.Align)
            {
                case TextAlign.Center:
                {
                    double c = (l.X + l.Right) / 2, wC = Math.Max(colW, need);
                    return new(c - wC / 2, l.Baseline, wC, 0);
                }
                case TextAlign.Right:
                {
                    double wR = Math.Max(colW, need);
                    return new(l.Right - wR, l.Baseline, wR, 0); // ölçüm kökenden mürekkebin sağına: sağ kenar birebir korunur
                }
                default:
                {
                    // Sola yaslı tek satır: sağdaki komşu metne (hücre/sütun) kadar uzayabilir; sonra kırılır.
                    double room = p.RightLimit > 0 ? p.RightLimit - l.X : double.MaxValue;
                    double wL = Math.Max(colW, Math.Min(need, room));
                    return new(l.X, l.Baseline, wL, 0);
                }
            }
        }

        return style.Align switch
        {
            // İki yana yaslı: satırlar özgün sağ kenara (pay eklemeden) yaslanır.
            TextAlign.Justify when p.Align == TextAlign.Justify => new(restX, L[0].Baseline, Math.Max(L.Max(l => l.Right) - restX, colW - Slack), indent),
            TextAlign.Center => new(L.Average(l => (l.X + l.Right) / 2) - colW / 2, L[0].Baseline, colW, 0),
            TextAlign.Right => new(L.Max(l => l.Right) - colW, L[0].Baseline, colW, 0),
            _ => new(restX, L[0].Baseline, colW, indent),
        };
    }

    /// <summary>Yeni metin kutusu ekler (sol üst köşe + genişlik).</summary>
    public Task<RectF> AddTextBlockAsync(int pageIndex, double left, double top, double width, string text, TextStyle style) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var r = LayoutText(page, text, style, left, top - style.Size * 0.8, Math.Max(width, style.Size * 2), style.Size * style.LineSpacing, tagBlock: true);
        Commit(pageIndex, page);
        return r;
    });

    private RectF LayoutText(nint page, string text, TextStyle style, double left, double firstBaseline, double width, double lineHeight,
        string? mark = null, double angleDeg = 0, double firstIndent = 0, nint fontHandle = 0, List<(nint Obj, string Text)>? created = null,
        IReadOnlyList<TextLineInfo>? original = null, bool tagBlock = false)
    {
        var font = fontHandle != 0 ? fontHandle : LoadFont(style.FontData);
        string? tag = tagBlock && Math.Abs(angleDeg) < 0.01 ? FormatBlock(new TextBlockInfo(style.Align, left, width, firstIndent)) : null;
        double M(string t) => Measure(t, font, style.Size);
        var lines = TextLayout.WrapEx(text, Math.Max(1, width - firstIndent), width, M);
        double a = angleDeg * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        RectF total = default;
        for (int i = 0; i < lines.Count; i++)
        {
            var (line, endsPara) = lines[i];
            if (line.Length == 0) continue;
            double indent = i == 0 ? firstIndent : 0;
            double avail = width - indent;
            // Metni değişmeyen iki yana yaslı satır, kendi özgün sağ kenarına yaslanır (alt-piksel birebir).
            double lw = M(line);
            if (style.Align == TextAlign.Justify && original is not null && i < original.Count && original[i].Text == line && Math.Abs(angleDeg) < 0.01
                && original[i].Right - (left + indent) >= lw)
                avail = original[i].Right - (left + indent);
            double dx = style.Align switch
            {
                TextAlign.Center => indent + (avail - lw) / 2,
                TextAlign.Right => indent + avail - lw,
                _ => indent,
            };
            // Satır kökeni: sütun solundan dx kadar sağda, ilk taban çizgisinden i satır aşağıda (döndürülmüşse eksenler boyunca).
            double ox = left + dx * cos + i * lineHeight * sin, oy = firstBaseline + dx * sin - i * lineHeight * cos;
            if (Math.Abs(angleDeg) < 0.01) { ox = left + dx; oy = firstBaseline - i * lineHeight; }
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double lineW = lw;
            if (style.Align == TextAlign.Justify && !endsPara && words.Length > 1 && avail >= lw && avail - lw < avail * 0.35)
            {
                // İki yana yasla (PDF'nin Tw sözcük aralığı gibi): satırın doğal genişliğinden kalan pay her boşluğa
                // eşit eklenir; sözcükler ayrı nesneler olarak kendi kesin konumlarına yerleştirilir.
                double extra = (avail - lw) / (words.Length - 1);
                for (int k = 0; k < words.Length; k++)
                {
                    // Sözcüğün doğal başlangıcı: (önceki sözcükler + bu sözcük) genişliği − bu sözcüğün genişliği.
                    double start = k == 0 ? 0 : M(string.Join(' ', words, 0, k + 1)) - M(words[k]);
                    double x = start + k * extra;
                    var o = CreateText(font, words[k], style, ox + x * cos, oy + x * sin, angleDeg, mark, tag);
                    Native.FPDFPage_InsertObject(page, o);
                    created?.Add((o, words[k]));
                    var bw = Bounds(o);
                    total = total.IsEmpty ? bw : total.Union(bw);
                }
                lineW = avail;
            }
            else
            {
                var o = CreateText(font, line, style, ox, oy, angleDeg, mark, tag);
                Native.FPDFPage_InsertObject(page, o);
                created?.Add((o, line));
                var bounds = Bounds(o);
                total = total.IsEmpty ? bounds : total.Union(bounds);
            }
            if (style.Underline)
            {
                double ux = ox + style.Size * 0.12 * sin, uy = oy - style.Size * 0.12 * cos;
                var path = Native.FPDFPageObj_CreateNewPath((float)ux, (float)uy);
                Native.FPDFPath_LineTo(path, (float)(ux + lineW * cos), (float)(uy + lineW * sin));
                Native.FPDFPageObj_SetStrokeColor(path, (style.Rgb >> 16) & 0xFF, (style.Rgb >> 8) & 0xFF, style.Rgb & 0xFF, style.Alpha);
                Native.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.5, style.Size * 0.06));
                Native.FPDFPath_SetDrawMode(path, Native.FPDF_FILLMODE_NONE, 1);
                AddMark(path, MarkUnderline);
                Native.FPDFPage_InsertObject(page, path);
                created?.Add((path, ""));
            }
        }
        return total;
    }

    private nint CreateText(nint font, string text, TextStyle style, double x, double y, double angleDeg, string? mark, string? blockTag = null)
    {
        var o = Native.FPDFPageObj_CreateTextObj(_doc, font, (float)style.Size);
        if (o == 0) throw new PdfException(PdfErrorKind.Unknown, "Metin nesnesi oluşturulamadı.");
        fixed (char* t = text + "\0") Native.FPDFText_SetText(o, (ushort*)t);
        Native.FPDFPageObj_SetFillColor(o, (style.Rgb >> 16) & 0xFF, (style.Rgb >> 8) & 0xFF, style.Rgb & 0xFF, style.Alpha);
        double a = angleDeg * Math.PI / 180;
        Native.FPDFPageObj_Transform(o, Math.Cos(a), Math.Sin(a), -Math.Sin(a), Math.Cos(a), x, y);
        if (mark is not null) { AddMark(o, mark); AddMark(o, MarkArtifact); }
        else if (blockTag is not null)
        {
            var nm = Encoding.ASCII.GetBytes(MarkText + "\0");
            var val = Encoding.ASCII.GetBytes(blockTag + "\0");
            fixed (byte* pn = nm) fixed (byte* pk = BoxKey) fixed (byte* pv = val)
            {
                var mk = Native.FPDFPageObj_AddMark(o, pn);
                if (mk != 0) Native.FPDFPageObjMark_SetStringParam(_doc, o, mk, pk, pv);
            }
        }
        return o;
    }

    private void AddMark(nint o, string name)
    {
        var n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = n) Native.FPDFPageObj_AddMark(o, p);
    }

    private static RectF Bounds(nint o)
    {
        Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
        return new RectF(l, b, r, t).Normalize();
    }

    private static bool Intersects(RectF a, RectF b) => a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    private static IEnumerable<nint> AllObjects(nint page)
    {
        int n = Native.FPDFPage_CountObjects(page);
        var list = new List<nint>(n);
        for (int i = 0; i < n; i++) { var o = Native.FPDFPage_GetObject(page, i); if (o != 0) list.Add(o); }
        return list;
    }

    private void Commit(int pageIndex, nint page)
    {
        GenerateContent(pageIndex, page);
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    }

    // ─────────────── Nesne işlemleri ───────────────

    private List<nint> Objects(nint page, IEnumerable<int> indices)
    {
        var list = new List<nint>();
        foreach (var i in indices.Distinct())
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0) throw new PdfException(PdfErrorKind.Page, $"Nesne {i} bulunamadı (belge değişmiş olabilir).");
            list.Add(o);
        }
        return list;
    }

    public Task DeleteObjectsAsync(int pageIndex, IEnumerable<int> indices) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var objs = Objects(page, indices);
        // Görünmez OCR metni silinirse görüntüdeki karşılığı da silinir (taranmış sayfada “metni sil”).
        foreach (var o in objs.Where(o => Native.FPDFPageObj_GetType(o) == Native.FPDF_PAGEOBJ_TEXT && Native.FPDFTextObj_GetTextRenderMode(o) == 3).ToList())
            EraseImageUnder(page, Bounds(o));
        foreach (var o in objs)
            if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
        Commit(pageIndex, page);
    });

    public Task MoveObjectsAsync(int pageIndex, IEnumerable<int> indices, double dx, double dy) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices)) Native.FPDFPageObj_Transform(o, 1, 0, 0, 1, dx, dy);
        Commit(pageIndex, page);
    });

    /// <summary>Nesneyi yeni sınırlara ölçekler (görsel, şekil veya metin).</summary>
    public Task ResizeObjectAsync(int pageIndex, int index, RectF target, bool keepAspect = false) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var o = Objects(page, new[] { index })[0];
        var b = Bounds(o);
        if (b.Width < 0.01 || b.Height < 0.01) throw new PdfException(PdfErrorKind.Unsupported, "Bu nesne boyutlandırılamaz.");
        double sx = target.Width / b.Width, sy = target.Height / b.Height;
        if (keepAspect) sx = sy = Math.Min(sx, sy);
        Native.FPDFPageObj_Transform(o, sx, 0, 0, sy, target.Left - b.Left * sx, target.Bottom - b.Bottom * sy);
        Commit(pageIndex, page);
    });

    public Task AlignObjectsAsync(int pageIndex, IEnumerable<int> indices, AlignMode mode) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var objs = Objects(page, indices);
        if (objs.Count < 2) return;
        var boxes = objs.Select(Bounds).ToList();
        double l = boxes.Min(b => b.Left), r = boxes.Max(b => b.Right), bt = boxes.Min(b => b.Bottom), tp = boxes.Max(b => b.Top);
        for (int k = 0; k < objs.Count; k++)
        {
            var b = boxes[k];
            double dx = mode switch { AlignMode.Left => l - b.Left, AlignMode.Right => r - b.Right, AlignMode.CenterH => (l + r) / 2 - (b.Left + b.Right) / 2, _ => 0 };
            double dy = mode switch { AlignMode.Top => tp - b.Top, AlignMode.Bottom => bt - b.Bottom, AlignMode.Middle => (bt + tp) / 2 - (b.Bottom + b.Top) / 2, _ => 0 };
            if (dx != 0 || dy != 0) Native.FPDFPageObj_Transform(objs[k], 1, 0, 0, 1, dx, dy);
        }
        Commit(pageIndex, page);
    });

    /// <summary>Metin veya şeklin dolgu rengini değiştirir.</summary>
    public Task SetFillColorAsync(int pageIndex, IEnumerable<int> indices, uint rgb) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices))
        {
            Native.FPDFPageObj_GetFillColor(o, out _, out _, out _, out var a);
            Native.FPDFPageObj_SetFillColor(o, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, a == 0 ? 255 : a);
        }
        Commit(pageIndex, page);
    });

    public Task SetStrokeAsync(int pageIndex, IEnumerable<int> indices, uint rgb, double width) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices))
        {
            Native.FPDFPageObj_SetStrokeColor(o, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
            if (width > 0) Native.FPDFPageObj_SetStrokeWidth(o, (float)width);
        }
        Commit(pageIndex, page);
    });

    // ─────────────── Şekil ve görsel ───────────────

    public Task AddShapeAsync(int pageIndex, ShapeKind kind, RectF rect, ShapeStyle style) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var r = rect.Normalize();
        float L = (float)r.Left, B = (float)r.Bottom, R = (float)r.Right, T = (float)r.Top;
        nint path;
        switch (kind)
        {
            case ShapeKind.Rectangle:
                path = Native.FPDFPageObj_CreateNewRect(L, B, R - L, T - B);
                break;
            case ShapeKind.Ellipse:
                {
                    // Dört Bézier eğrisiyle elips (k = 0.5523).
                    float cx = (L + R) / 2, cy = (B + T) / 2, rx = (R - L) / 2, ry = (T - B) / 2, k = 0.5523f;
                    path = Native.FPDFPageObj_CreateNewPath(cx + rx, cy);
                    Native.FPDFPath_BezierTo(path, cx + rx, cy + k * ry, cx + k * rx, cy + ry, cx, cy + ry);
                    Native.FPDFPath_BezierTo(path, cx - k * rx, cy + ry, cx - rx, cy + k * ry, cx - rx, cy);
                    Native.FPDFPath_BezierTo(path, cx - rx, cy - k * ry, cx - k * rx, cy - ry, cx, cy - ry);
                    Native.FPDFPath_BezierTo(path, cx + k * rx, cy - ry, cx + rx, cy - k * ry, cx + rx, cy);
                    Native.FPDFPath_Close(path);
                    break;
                }
            default:
                {
                    // Çizgi / ok: rect.Left,Bottom → rect.Right,Top yönünde (Normalize öncesi yön korunur).
                    float x1 = (float)rect.Left, y1 = (float)rect.Bottom, x2 = (float)rect.Right, y2 = (float)rect.Top;
                    path = Native.FPDFPageObj_CreateNewPath(x1, y1);
                    Native.FPDFPath_LineTo(path, x2, y2);
                    if (kind == ShapeKind.Arrow)
                    {
                        double ang = Math.Atan2(y2 - y1, x2 - x1), len = Math.Max(6, style.StrokeWidth * 5);
                        for (int sgn = -1; sgn <= 1; sgn += 2)
                        {
                            double a2 = ang + Math.PI - sgn * 0.45;
                            Native.FPDFPath_MoveTo(path, x2, y2);
                            Native.FPDFPath_LineTo(path, (float)(x2 + len * Math.Cos(a2)), (float)(y2 + len * Math.Sin(a2)));
                        }
                    }
                    break;
                }
        }
        Native.FPDFPageObj_SetStrokeColor(path, (style.StrokeRgb >> 16) & 0xFF, (style.StrokeRgb >> 8) & 0xFF, style.StrokeRgb & 0xFF, style.Alpha);
        Native.FPDFPageObj_SetStrokeWidth(path, (float)style.StrokeWidth);
        bool fill = style.FillRgb is not null && kind is ShapeKind.Rectangle or ShapeKind.Ellipse;
        if (fill) Native.FPDFPageObj_SetFillColor(path, (style.FillRgb!.Value >> 16) & 0xFF, (style.FillRgb.Value >> 8) & 0xFF, style.FillRgb.Value & 0xFF, style.Alpha);
        Native.FPDFPath_SetDrawMode(path, fill ? Native.FPDF_FILLMODE_WINDING : Native.FPDF_FILLMODE_NONE, style.StrokeWidth > 0 ? 1 : 0);
        Native.FPDFPage_InsertObject(page, path);
        Commit(pageIndex, page);
    });

    /// <summary>Görsel ekler; görsel <paramref name="rect"/> içine orantılı sığdırılır.</summary>
    public Task<RectF> AddImageAsync(int pageIndex, ImagePageSource img, RectF rect) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var obj = Native.FPDFPageObj_NewImageObj(_doc);
        try
        {
            var (w, h) = FillImage(obj, img, page);
            var target = Fit(rect.Normalize(), w, h);
            Native.FPDFImageObj_SetMatrix(obj, target.Width, 0, 0, target.Height, target.Left, target.Bottom);
            Native.FPDFPage_InsertObject(page, obj);
            obj = 0;
            Commit(pageIndex, page);
            return target;
        }
        finally { if (obj != 0) Native.FPDFPageObj_Destroy(obj); }
    });

    /// <summary>Mevcut görselin içeriğini değiştirir; yeni görsel eskisinin alanına orantılı sığdırılır.</summary>
    public Task ReplaceImageAsync(int pageIndex, int index, ImagePageSource img) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var o = Objects(page, new[] { index })[0];
        if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_IMAGE) throw new PdfException(PdfErrorKind.Unsupported, "Seçili nesne bir görsel değil.");
        var old = Bounds(o);
        var (w, h) = FillImage(o, img, page);
        var target = Fit(old, w, h);
        Native.FPDFImageObj_SetMatrix(o, target.Width, 0, 0, target.Height, target.Left, target.Bottom);
        Commit(pageIndex, page);
    });

    private static RectF Fit(RectF box, int pxW, int pxH)
    {
        double s = Math.Min(box.Width / pxW, box.Height / pxH);
        double w = pxW * s, h = pxH * s;
        double x = box.Left + (box.Width - w) / 2, y = box.Bottom + (box.Height - h) / 2;
        return new RectF(x, y, x + w, y + h);
    }

    private (int W, int H) FillImage(nint obj, ImagePageSource img, nint page)
    {
        var pages = stackalloc nint[1];
        pages[0] = page;
        switch (img)
        {
            case JpegImageSource j:
                using (var src = new StreamSource(new MemoryStream(j.Data, false)))
                    if (Native.FPDFImageObj_LoadJpegFileInline(pages, 1, obj, src.Access) == 0) throw new PdfException(PdfErrorKind.Format, "JPEG görüntüsü okunamadı.");
                Native.FPDFImageObj_GetImagePixelSize(obj, out var w, out var h);
                return ((int)w, (int)h);
            case BitmapImageSource b:
                var bgr = ToBgrOnWhite(b.Image, out int stride);
                fixed (byte* p = bgr)
                {
                    var bmp = Native.FPDFBitmap_CreateEx(b.Image.Width, b.Image.Height, Native.FPDFBitmap_BGR, p, stride);
                    try { if (Native.FPDFImageObj_SetBitmap(pages, 1, obj, bmp) == 0) throw new PdfException(PdfErrorKind.Unknown, "Görüntü eklenemedi."); }
                    finally { Native.FPDFBitmap_Destroy(bmp); }
                }
                return (b.Image.Width, b.Image.Height);
            default: throw new ArgumentException("Bilinmeyen görüntü türü.");
        }
    }

    // ─────────────── Bağlantılar ───────────────

    public Task AddLinkAsync(int pageIndex, RectF rect, string uri) => _engine.RunAsync(() =>
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto"))
            throw new PdfException(PdfErrorKind.Unsupported, "Bağlantı http://, https:// veya mailto: ile başlamalı.");
        var page = Page(pageIndex);
        var annot = Native.FPDFPage_CreateAnnot(page, Native.FPDF_ANNOT_LINK);
        if (annot == 0) throw new PdfException(PdfErrorKind.Unknown, "Bağlantı oluşturulamadı.");
        try
        {
            var r = rect.Normalize();
            var fr = new FS_RECTF { left = (float)r.Left, bottom = (float)r.Bottom, right = (float)r.Right, top = (float)r.Top };
            Native.FPDFAnnot_SetRect(annot, &fr);
            Native.FPDFAnnot_SetBorder(annot, 0, 0, 0);
            var bytes = Encoding.ASCII.GetBytes(u.AbsoluteUri + "\0");
            fixed (byte* p = bytes) if (Native.FPDFAnnot_SetURI(annot, p) == 0) throw new PdfException(PdfErrorKind.Unknown, "Bağlantı adresi yazılamadı.");
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    });

    /// <summary>Noktadaki bağlantı açıklamasını (annotation) sayfadan kaldırır. Kaldırıldıysa true.</summary>
    public Task<bool> RemoveLinkAtAsync(int pageIndex, double x, double y) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        int n = Native.FPDFPage_GetAnnotCount(page);
        for (int i = n - 1; i >= 0; i--)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            bool hit = false;
            try
            {
                if (Native.FPDFAnnot_GetSubtype(a) == Native.FPDF_ANNOT_LINK && Native.FPDFAnnot_GetRect(a, out var r) != 0)
                    hit = new RectF(r.left, r.bottom, r.right, r.top).Normalize().Contains(x, y);
            }
            finally { Native.FPDFPage_CloseAnnot(a); }
            if (hit && Native.FPDFPage_RemoveAnnot(page, i) != 0)
            {
                ClosePage(pageIndex);
                MarkChanged(structure: false);
                return true;
            }
        }
        return false;
    });

    // ─────────────── Filigran, üst/altbilgi, arka plan ───────────────

    /// <summary>Görüntülenen yöndeki (X sağa, Y yukarı, pt) bir noktayı kullanıcı alanına çevirir; döndürme açısını verir.</summary>
    private static (double X, double Y, double Angle) Displayed(nint page, double dispX, double dispY, out double dispW, out double dispH)
    {
        var box = GetEffectiveBox(page);
        int rot = Native.FPDFPage_GetRotation(page);
        var g = new PageGeometry(box, rot);
        bool swap = rot % 2 == 1;
        dispW = swap ? box.Height : box.Width; dispH = swap ? box.Width : box.Height;
        var (x, y) = g.FromUnit(dispX / dispW, 1 - dispY / dispH);
        return (x, y, rot * 90.0);
    }

    public Task AddWatermarkAsync(IEnumerable<int> pages, WatermarkSettings s) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        var font = LoadFont(s.Style.FontData);
        foreach (var i in arr)
        {
            var page = Page(i);
            Displayed(page, 0, 0, out var W, out var H);
            double size = s.Style.Size > 0 ? s.Style.Size : Math.Max(12, Math.Sqrt(W * W + H * H) * 0.65 / Math.Max(1, Measure(s.Text, font, 1)));
            double tw = Measure(s.Text, font, size), th = size * 0.7;
            double a = s.AngleDegrees * Math.PI / 180;
            // Metnin merkezi sayfa merkezine gelsin.
            double ox = W / 2 - (tw / 2 * Math.Cos(a) - th / 2 * Math.Sin(a));
            double oy = H / 2 - (tw / 2 * Math.Sin(a) + th / 2 * Math.Cos(a));
            var (ux, uy, rotAngle) = Displayed(page, ox, oy, out _, out _);
            var style = s.Style with { Size = size, Alpha = (byte)Math.Clamp(s.Opacity * 255, 10, 255) };
            var o = CreateText(font, s.Text, style, ux, uy, s.AngleDegrees + rotAngle, MarkWatermark);
            Native.FPDFPage_InsertObject(page, o);
            Commit(i, page);
        }
    });

    /// <summary>Mesly ile eklenmiş (veya standart “Artifact” filigranı olarak işaretlenmiş) filigranları siler. Silinen nesne sayısı.</summary>
    public Task<int> RemoveWatermarksAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkWatermark);
    public Task<int> RemoveHeaderFooterAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkHeaderFooter);
    public Task<int> RemoveBackgroundAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkBackground);

    private Task<int> RemoveByRole(IEnumerable<int> pages, string role) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        int removed = 0;
        foreach (var i in arr)
        {
            var page = Page(i);
            var victims = AllObjects(page).Where(o => RoleOf(o) == role).ToList();
            foreach (var o in victims) if (Native.FPDFPage_RemoveObject(page, o) != 0) { Native.FPDFPageObj_Destroy(o); removed++; }
            if (victims.Count > 0) Commit(i, page);
        }
        return removed;
    });

    public Task AddHeaderFooterAsync(IEnumerable<int> pages, HeaderFooterSettings s) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        var font = LoadFont(s.Style.FontData);
        var now = DateTime.Now;
        int total = arr.Length;
        for (int k = 0; k < arr.Length; k++)
        {
            int i = arr[k];
            var page = Page(i);
            Displayed(page, 0, 0, out var W, out var H);
            foreach (var item in s.Items)
            {
                var text = TextLayout.Fill(item.Template, s.StartNumber + k, s.StartNumber + total - 1, s.FileName, now);
                if (string.IsNullOrWhiteSpace(text)) continue;
                double tw = Measure(text, font, s.Style.Size);
                double x = item.Position switch
                {
                    HFPosition.TopLeft or HFPosition.BottomLeft => s.MarginPt,
                    HFPosition.TopRight or HFPosition.BottomRight => W - s.MarginPt - tw,
                    _ => (W - tw) / 2,
                };
                bool top = item.Position is HFPosition.TopLeft or HFPosition.TopCenter or HFPosition.TopRight;
                double y = top ? H - s.MarginPt - s.Style.Size * 0.8 : s.MarginPt;
                var (ux, uy, rot) = Displayed(page, x, y, out _, out _);
                var o = CreateText(font, text, s.Style, ux, uy, rot, MarkHeaderFooter);
                Native.FPDFPage_InsertObject(page, o);
            }
            Commit(i, page);
        }
    });

    /// <summary>Sayfanın en altına (tüm içeriğin arkasına) renkli arka plan ekler.</summary>
    public Task SetBackgroundAsync(IEnumerable<int> pages, uint rgb) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        foreach (var i in arr)
        {
            var page = Page(i);
            foreach (var old in AllObjects(page).Where(o => RoleOf(o) == MarkBackground).ToList())
                if (Native.FPDFPage_RemoveObject(page, old) != 0) Native.FPDFPageObj_Destroy(old);
            var box = GetEffectiveBox(page);
            var rect = Native.FPDFPageObj_CreateNewRect((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height);
            Native.FPDFPageObj_SetFillColor(rect, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
            Native.FPDFPath_SetDrawMode(rect, Native.FPDF_FILLMODE_WINDING, 0);
            AddMark(rect, MarkBackground);
            AddMark(rect, MarkArtifact);
            Native.FPDFPage_InsertObjectAtIndex(page, rect, 0);
            Commit(i, page);
        }
    });
}
