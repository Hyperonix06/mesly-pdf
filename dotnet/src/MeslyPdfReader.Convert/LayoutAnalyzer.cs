using System.Text;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Sayfa düzeni çözümleme: karakterler → sözcükler → satırlar; çizgili tablolar (sayfa görüntüsündeki çizgilerden);
/// okuma sırası (XY-kesme, sütun ayrımı); paragraf, başlık, hizalama ve satır aralığı.
/// Metin PDF'de karakterler doğrudan PDF'den, taranmış PDF'de OCR metin katmanından gelir.
/// </summary>
public static class LayoutAnalyzer
{
    internal sealed record Word(string Text, Box Box, double Baseline, double Size, string? Font, bool Bold, bool Italic, uint Rgb, int Flags = 0);
    internal sealed record Line(List<Word> Words, Box Box, double Baseline, double Size);
    private sealed record Unit(Box Box, Line? Line = null, LayoutBlock? Block = null);

    public sealed record Options(bool DetectTables = true, bool IncludeImages = true, int TableDpi = 100);

    public static async Task<PageLayout> AnalyzeAsync(PdfDocument doc, int page, Options? o = null, CancellationToken ct = default)
    {
        o ??= new Options();
        var size = await doc.GetDisplaySizeAsync(page);
        var chars = await doc.GetStyledCharsAsync(page, ct);
        bool fromOcr = chars.Count > 0 && chars.Count(c => c.Invisible) > chars.Count / 2;
        var words = BuildWords(chars);

        var blocks = new List<LayoutBlock>();
        var tables = new List<TableBlock>();
        if (o.DetectTables && words.Count > 0)
        {
            List<TableDetector.Grid> grids;
            if (fromOcr)
            {
                // Taranmış sayfa: çizgiler yalnızca piksellerde.
                var img = await PdfTools.RenderAtDpiAsync(doc, page, o.TableDpi, ct);
                grids = TableDetector.Detect(ImageOps.ToGray(img), 72.0 / o.TableDpi);
            }
            else
            {
                // Metin PDF: çizgiler vektör nesneleri olarak doğrudan okunur (kesin konum, eşik/çözünürlük sorunu yok).
                grids = TableDetector.DetectFromLines(await doc.GetVectorLinesAsync(page, ct));
                if (grids.Count == 0)
                {
                    var img = await PdfTools.RenderAtDpiAsync(doc, page, o.TableDpi, ct);
                    grids = TableDetector.Detect(ImageOps.ToGray(img), 72.0 / o.TableDpi);
                }
            }
            foreach (var g in grids)
            {
                var inside = words.Where(w => g.Box.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
                if (inside.Count == 0) continue;
                tables.Add(FillTable(g, inside));
                words.RemoveAll(w => g.Box.Contains(w.Box.CenterX, w.Box.CenterY));
            }
            // Çizgisiz tablolar: kalan sözcüklerin hizalamasından.
            foreach (var g in BorderlessTables.Detect(words))
            {
                var inside = words.Where(w => g.Box.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
                if (inside.Count == 0) continue;
                tables.Add(FillTable(g, inside));
                words.RemoveAll(w => g.Box.Contains(w.Box.CenterX, w.Box.CenterY));
            }
        }

        var images = new List<ImageBlock>();
        if (o.IncludeImages)
        {
            foreach (var im in await doc.GetPageImagesAsync(page, 12, ct))
            {
                // Taranmış sayfanın kendisi (sayfayı kaplayan görüntü) görsel olarak aktarılmaz; metni zaten tanındı.
                double cover = im.Box.Width * im.Box.Height / (size.Width * size.Height);
                if (fromOcr && cover > 0.6) continue;
                if (cover > 0.92 && words.Count > 20) continue; // arka plan görüntüsü
                images.Add(new ImageBlock(im.Box, im.Pixels, im.IsJpeg));
            }
        }

        if (fromOcr && o.IncludeImages)
        {
            var pics = await ScannedPictureRegionsAsync(doc, page, size, words, tables, ct);
            images.AddRange(pics);
            // Resmin içindeki yazılar (ör. logo yazısı) resmin parçasıdır; metin akışına girmez.
            words.RemoveAll(w => pics.Any(p => p.Box.Contains(w.Box.CenterX, w.Box.CenterY)));
        }

        // Sütun aralıkları bir satırı tek parça yapmasın diye önce dar eşikle parçala; XY-kesimden sonra aynı bölgedeki parçalar birleşir.
        var lines = BuildLines(words, 0.8);
        var units = lines.Select(l => new Unit(l.Box, l))
            .Concat(tables.Select(t => new Unit(t.Box, Block: t)))
            .Concat(images.Select(i => new Unit(i.Box, Block: i))).ToList();
        double bodySize = BodySize(words);
        var ordered = new List<List<Unit>>();
        var colOf = new Dictionary<List<Unit>, (int Group, int Idx)>(ReferenceEqualityComparer.Instance);
        var broken = new HashSet<int>();
        int groupCounter = 0;
        XyCut(units, ordered, bodySize, 0, null, colOf, broken, ref groupCounter);

        LayoutBlock? prev = null;
        var textArea = units.Count == 0 ? new Box(0, 0, size.Width, size.Height) : units.Select(u => u.Box).Aggregate((a, b) => a.Union(b));
        // Gerçek sütun kümeleri: bozulmamış, en az 2 sütunlu ve her sütunda en az 3 satır metin olanlar.
        var validGroups = new Dictionary<int, List<Box>>();
        foreach (var gr in colOf.GroupBy(kv => kv.Value.Group).Where(gr => !broken.Contains(gr.Key)))
        {
            var cols = gr.GroupBy(kv => kv.Value.Idx).OrderBy(c => c.Key)
                .Select(c => (Lines: c.Sum(kv => kv.Key.Count(u => u.Line is not null)), Box: c.SelectMany(kv => kv.Key).Select(u => u.Box).Aggregate((a, b) => a.Union(b)))).ToList();
            if (cols.Count >= 2 && cols.All(c => c.Lines >= 3)) validGroups[gr.Key] = cols.Select(c => c.Box).ToList();
        }
        int activeGroup = -1, activeIdx = -1;
        void CloseColumns()
        {
            if (activeGroup < 0) return;
            blocks.Add(new ColumnsBlock(textArea, ColumnsKind.End, Array.Empty<double>(), Array.Empty<double>()));
            activeGroup = -1; activeIdx = -1; prev = null;
        }
        foreach (var region in ordered)
        {
            var regionBox = region.Select(u => u.Box).Aggregate((a, b) => a.Union(b));
            if (colOf.TryGetValue(region, out var ci) && validGroups.TryGetValue(ci.Group, out var colBoxes))
            {
                if (activeGroup != ci.Group)
                {
                    CloseColumns();
                    var widths = colBoxes.Select(b => b.Width).ToList();
                    var gaps = Enumerable.Range(0, colBoxes.Count - 1).Select(i => Math.Max(6, colBoxes[i + 1].X0 - colBoxes[i].X1)).ToList();
                    blocks.Add(new ColumnsBlock(colBoxes.Aggregate((a, b) => a.Union(b)), ColumnsKind.Start, widths, gaps));
                    activeGroup = ci.Group; activeIdx = ci.Idx; prev = null;
                }
                else if (activeIdx != ci.Idx)
                {
                    blocks.Add(new ColumnsBlock(regionBox, ColumnsKind.Break, Array.Empty<double>(), Array.Empty<double>()));
                    activeIdx = ci.Idx; prev = null;
                }
            }
            else CloseColumns();
            var pending = new List<Line>();
            void Flush()
            {
                if (pending.Count == 0) return;
                pending = MergeFragments(pending);
                // Tek satırlık dar bölgelerde (başlık) hizalama sayfa metin alanına göre belirlenir.
                var alignArea = regionBox.Width < textArea.Width * 0.6 && pending.Count == 1 ? textArea : regionBox;
                foreach (var p in BuildParagraphs(pending, regionBox, bodySize, size.Width, alignArea))
                {
                    var sp = prev is null ? 0 : Math.Clamp(p.Box.Y0 - prev.Box.Y1, 0, 48);
                    var withSpace = p with { SpaceBefore = sp };
                    blocks.Add(withSpace); prev = withSpace;
                }
                pending.Clear();
            }
            foreach (var u in region.OrderBy(u => u.Box.Y0).ThenBy(u => u.Box.X0))
            {
                if (u.Line is not null) { pending.Add(u.Line); continue; }
                Flush();
                blocks.Add(u.Block!); prev = u.Block;
            }
            Flush();
        }
        CloseColumns();
        return new PageLayout(page, size.Width, size.Height, blocks, fromOcr);
    }

    /// <summary>
    /// Taranmış sayfada metin ve tablo dışında kalan koyu/renkli alanları (fotoğraf, logo, şekil) görsel olarak keser.
    /// Sayfa 4 pt'lik hücrelere bölünür; mürekkepli hücrelerin bağlı bileşenleri en az 36×36 pt ise görseldir.
    /// </summary>
    private static async Task<List<ImageBlock>> ScannedPictureRegionsAsync(PdfDocument doc, int page, SizeF size, List<Word> words, List<TableBlock> tables, CancellationToken ct)
    {
        const int dpi = 150; const double cell = 4;
        var img = await PdfTools.RenderAtDpiAsync(doc, page, dpi, ct);
        double k = dpi / 72.0;
        int cw = (int)Math.Ceiling(size.Width / cell), chh = (int)Math.Ceiling(size.Height / cell);
        var ink = new bool[cw, chh];
        var blocked = new bool[cw, chh];
        void Block(Box b, double pad)
        {
            for (int x = Math.Max(0, (int)((b.X0 - pad) / cell)); x < Math.Min(cw, (int)Math.Ceiling((b.X1 + pad) / cell)); x++)
                for (int y = Math.Max(0, (int)((b.Y0 - pad) / cell)); y < Math.Min(chh, (int)Math.Ceiling((b.Y1 + pad) / cell)); y++) blocked[x, y] = true;
        }
        foreach (var t in tables) Block(t.Box, 2);
        // Sözcük kutularının kapladığı hücreler (resim/metin ayrımı için).
        var textCell = new bool[cw, chh];
        foreach (var w in words)
            for (int x = Math.Max(0, (int)(w.Box.X0 / cell)); x < Math.Min(cw, (int)Math.Ceiling(w.Box.X1 / cell)); x++)
                for (int y = Math.Max(0, (int)(w.Box.Y0 / cell)); y < Math.Min(chh, (int)Math.Ceiling(w.Box.Y1 / cell)); y++) textCell[x, y] = true;
        for (int cx = 0; cx < cw; cx++)
            for (int cy = 0; cy < chh; cy++)
            {
                if (blocked[cx, cy]) continue;
                int x0 = (int)(cx * cell * k), y0 = (int)(cy * cell * k), x1 = Math.Min(img.Width, (int)((cx + 1) * cell * k)), y1 = Math.Min(img.Height, (int)((cy + 1) * cell * k));
                int n = 0, dark = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int o = y * img.Stride + x * 4; n++;
                        if (Math.Min(img.Pixels[o], Math.Min(img.Pixels[o + 1], img.Pixels[o + 2])) < 225) dark++;
                    }
                ink[cx, cy] = n > 0 && dark > n * 0.08;
            }
        var seen = new bool[cw, chh];
        var result = new List<ImageBlock>();
        for (int sx = 0; sx < cw; sx++)
            for (int sy = 0; sy < chh; sy++)
            {
                if (!ink[sx, sy] || seen[sx, sy]) continue;
                int minX = sx, maxX = sx, minY = sy, maxY = sy, count = 0, textCount = 0;
                var q = new Queue<(int, int)>(); q.Enqueue((sx, sy)); seen[sx, sy] = true;
                while (q.Count > 0)
                {
                    var (x, y) = q.Dequeue(); count++;
                    if (textCell[x, y]) textCount++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    // 1 hücrelik boşluğu köprüle (açık renkli fotoğraf içleri)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= cw || ny >= chh || seen[nx, ny] || !ink[nx, ny]) continue;
                            seen[nx, ny] = true; q.Enqueue((nx, ny));
                        }
                }
                double bw = (maxX - minX + 1) * cell, bh = (maxY - minY + 1) * cell;
                if (bw < 36 || bh < 36 || count < 40) continue;
                // Büyük ölçüde sözcük kutularından oluşan bileşen metindir, resim değil.
                if (textCount > count * 0.35) continue;
                var box = new Box(minX * cell, minY * cell, (maxX + 1) * cell, (maxY + 1) * cell);
                int px0 = (int)(box.X0 * k), py0 = (int)(box.Y0 * k), pw = Math.Min(img.Width - px0, (int)(box.Width * k)), ph = Math.Min(img.Height - py0, (int)(box.Height * k));
                var crop = new byte[pw * ph * 4];
                for (int y = 0; y < ph; y++) Buffer.BlockCopy(img.Pixels, (py0 + y) * img.Stride + px0 * 4, crop, y * pw * 4, pw * 4);
                result.Add(new ImageBlock(box, new BgraImage { Width = pw, Height = ph, Stride = pw * 4, Pixels = crop }, true));
            }
        return result;
    }

    // ─────────────── Sözcükler ve satırlar ───────────────

    internal static List<Word> BuildWords(IReadOnlyList<StyledChar> chars)
    {
        var words = new List<Word>();
        var sb = new StringBuilder();
        StyledChar? first = null, last = null;
        Box box = default;
        void Emit()
        {
            if (sb.Length > 0 && first is not null)
                words.Add(new Word(sb.ToString(), box, first.Baseline, first.Size, first.FontName, first.Bold, first.Italic, first.Rgb, first.FontFlags));
            sb.Clear(); first = null; last = null; box = default;
        }
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c.Char)) { Emit(); continue; }
            if (last is not null)
            {
                double gap = c.Box.X0 - last.Box.X1;
                bool newLine = Math.Abs(c.Baseline - last.Baseline) > 0.45 * Math.Max(c.Size, last.Size);
                bool styleChange = c.Bold != last.Bold || c.Italic != last.Italic || Math.Abs(c.Size - last.Size) > 0.2 * last.Size;
                if (newLine || gap > 0.2 * c.Size || gap < -c.Size * 2 || styleChange) Emit();
            }
            if (first is null) { first = c; box = c.Box; } else box = box.Union(c.Box);
            sb.Append(c.Char);
            last = c;
        }
        Emit();
        return words;
    }

    internal static List<Line> BuildLines(List<Word> words, double maxGapEm = 2.2)
    {
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderBy(w => Math.Round(w.Baseline)).ThenBy(w => w.Box.X0))
        {
            var line = lines.LastOrDefault(L =>
            {
                var lw = L[^1];
                return Math.Abs(lw.Baseline - w.Baseline) <= 0.4 * Math.Max(lw.Size, w.Size)
                    && w.Box.X0 >= lw.Box.X0 && w.Box.X0 - lw.Box.X1 < maxGapEm * Math.Max(w.Size, lw.Size);
            });
            if (line is null) lines.Add(new List<Word> { w }); else line.Add(w);
        }
        return lines.Select(L =>
        {
            L.Sort((a, b) => a.Box.X0.CompareTo(b.Box.X0));
            var box = L.Select(w => w.Box).Aggregate((a, b) => a.Union(b));
            return new Line(L, box, L.Average(w => w.Baseline), Median(L.Select(w => w.Size)));
        }).ToList();
    }

    /// <summary>XY-kesimden sonra aynı bölgede, aynı taban çizgisinde yan yana kalan parçaları (≤ 2,2 em boşluk) tek satırda birleştirir.</summary>
    private static List<Line> MergeFragments(List<Line> lines)
    {
        var all = lines.SelectMany(l => l.Words).ToList();
        return BuildLines(all, 2.2);
    }

    private static double BodySize(List<Word> words)
    {
        if (words.Count == 0) return 11;
        // Karakter sayısıyla ağırlıklı en yaygın boyut.
        return words.GroupBy(w => Math.Round(w.Size * 2) / 2).OrderByDescending(g => g.Sum(w => w.Text.Length)).First().Key;
    }

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    // ─────────────── Okuma sırası (XY-kesme) ───────────────

    /// <summary>Bir sütun kümesi içindeki konum: grup numarası ve sütun sırası; <c>Continuation</c> = bu çağrı kümenin "geri kalan" sağ tarafı.</summary>
    private readonly record struct ColCtx(int Group, int Idx, bool Continuation);

    private static void XyCut(List<Unit> units, List<List<Unit>> output, double bodySize, int depth,
        ColCtx? ctx, Dictionary<List<Unit>, (int Group, int Idx)> colOf, HashSet<int> broken, ref int groupCounter)
    {
        if (units.Count == 0) return;
        if (units.Count == 1 || depth > 30) { Emit(units); return; }
        void Emit(List<Unit> u)
        {
            output.Add(u);
            if (ctx is { } c) colOf[u] = (c.Group, c.Idx);
        }
        // Önce sütun aralığı (dikey boşluk) ara: her iki yanda da yeterli içerik olmalı.
        var vgap = BestGap(units, vertical: true, minGap: Math.Max(8, bodySize * 0.75));
        if (vgap is { } vx)
        {
            var left = units.Where(u => u.Box.CenterX < vx).ToList();
            var right = units.Where(u => u.Box.CenterX >= vx).ToList();
            if (left.Count >= 2 && right.Count >= 2 && Overlap(left, right) > 0.3 && !MarkersOnly(left) && !MarkersOnly(right))
            {
                if (ctx is { Continuation: true } cc)
                {
                    // Aynı sütun kümesinin devamı: 3., 4. sütun…
                    XyCut(left, output, bodySize, depth + 1, new ColCtx(cc.Group, cc.Idx, false), colOf, broken, ref groupCounter);
                    XyCut(right, output, bodySize, depth + 1, new ColCtx(cc.Group, cc.Idx + 1, true), colOf, broken, ref groupCounter);
                }
                else
                {
                    if (ctx is { } outer) broken.Add(outer.Group); // sütunun içinde yeniden sütun: yapı karmaşık, sıralı bırak
                    int g = groupCounter++;
                    XyCut(left, output, bodySize, depth + 1, new ColCtx(g, 0, false), colOf, broken, ref groupCounter);
                    XyCut(right, output, bodySize, depth + 1, new ColCtx(g, 1, true), colOf, broken, ref groupCounter);
                }
                return;
            }
        }
        // Yatay boşluklar: bölgeyi üstten alta bantlara ayır.
        var bands = Bands(units, Math.Max(4, bodySize * 0.9));
        if (bands.Count > 1)
        {
            foreach (var b in bands) XyCut(b, output, bodySize, depth + 1, ctx is { Continuation: true } k ? k with { Continuation = false } : ctx, colOf, broken, ref groupCounter);
            return;
        }
        Emit(units);
    }

    /// <summary>Yalnız madde işareti/numara satırlarından oluşan dar sütun (liste işaretleri metinden ayrılmasın).</summary>
    private static bool MarkersOnly(List<Unit> g) =>
        g.All(u => u.Line is { Words.Count: 1 } l && IsListMarker(l.Words[0].Text, true) && l.Words[0].Text.Length <= 4);

    /// <summary>İki grubun dikey kesişim oranı (sütunlar yan yana mı).</summary>
    private static double Overlap(List<Unit> a, List<Unit> b)
    {
        double a0 = a.Min(u => u.Box.Y0), a1 = a.Max(u => u.Box.Y1), b0 = b.Min(u => u.Box.Y0), b1 = b.Max(u => u.Box.Y1);
        double inter = Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));
        return inter / Math.Max(1, Math.Min(a1 - a0, b1 - b0));
    }

    private static double? BestGap(List<Unit> units, bool vertical, double minGap)
    {
        var iv = units.Select(u => vertical ? (u.Box.X0, u.Box.X1) : (u.Box.Y0, u.Box.Y1)).OrderBy(i => i.Item1).ToList();
        double end = iv[0].Item2, bestW = 0; double? best = null;
        foreach (var (s, e) in iv.Skip(1))
        {
            if (s > end && s - end >= minGap && s - end > bestW) { bestW = s - end; best = (s + end) / 2; }
            end = Math.Max(end, e);
        }
        return best;
    }

    /// <summary>Üstten alta bantlar: yalnız <paramref name="minGap"/>'ten büyük yatay boşluklarda ayırır (normal satır aralığı ayırmaz).</summary>
    private static List<List<Unit>> Bands(List<Unit> units, double minGap)
    {
        var sorted = units.OrderBy(u => u.Box.Y0).ToList();
        var bands = new List<List<Unit>>();
        List<Unit>? cur = null; double end = double.MinValue;
        foreach (var u in sorted)
        {
            if (cur is null || u.Box.Y0 > end + minGap) { cur = new List<Unit>(); bands.Add(cur); end = u.Box.Y1; }
            cur.Add(u);
            end = Math.Max(end, u.Box.Y1);
        }
        return bands;
    }

    // ─────────────── Paragraflar ───────────────

    private static IEnumerable<ParagraphBlock> BuildParagraphs(List<Line> lines, Box region, double bodySize, double pageWidth, Box? alignArea = null)
    {
        lines.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));
        var groups = new List<List<Line>>();
        foreach (var l in lines)
        {
            var g = groups.LastOrDefault();
            if (g is null) { groups.Add(new List<Line> { l }); continue; }
            var p = g[^1];
            double dy = l.Baseline - p.Baseline;
            double step = g.Count >= 2 ? g[^1].Baseline - g[^2].Baseline : p.Size * 1.25;
            bool sizeChange = Math.Abs(l.Size - p.Size) > 0.15 * p.Size;
            bool boldChange = AllBold(l) != AllBold(p);
            bool bigGap = dy > Math.Max(step * 1.35, p.Size * 1.65);
            bool prevShortEnd = p.Box.X1 < region.X0 + (region.X1 - region.X0) * 0.72 && EndsSentence(p);
            bool indent = l.Box.X0 > g[0].Box.X0 + 1.2 * l.Size && g.Count >= 1 && l.Box.X0 > p.Box.X0 + 1.2 * l.Size;
            // Liste öğesi: yeni işaret yeni öğe başlatır; işaretin metin başlangıcına hizalı devam satırı aynı öğede kalır.
            bool newItem = l.Size < bodySize * 1.22 && IsListMarker(l.Words[0].Text, l.Words.Count > 1) && l.Words.Count > 1;
            if (!newItem && g[0].Size < bodySize * 1.22 && g[0].Words.Count > 1 && IsListMarker(g[0].Words[0].Text, true))
            {
                double textX = g[0].Words[1].Box.X0;
                if (Math.Abs(l.Box.X0 - textX) < 0.8 * l.Size || Math.Abs(l.Box.X0 - g[0].Box.X0) < 0.4 * l.Size && g.Count > 1) { indent = false; prevShortEnd = false; }
            }
            if (sizeChange || boldChange || bigGap || prevShortEnd || indent || newItem) groups.Add(new List<Line> { l });
            else g.Add(l);
        }
        foreach (var g in groups)
        {
            var runs = new List<TextRun>();
            string? marker = null; double markerOffset = 0;
            if (g[0].Size < bodySize * 1.22 && g[0].Words.Count > 1 && IsListMarker(g[0].Words[0].Text, true))
            {
                marker = g[0].Words[0].Text; markerOffset = Math.Max(4, g[0].Words[1].Box.X0 - g[0].Words[0].Box.X0);
            }
            for (int i = 0; i < g.Count; i++)
            {
                var ws = g[i].Words;
                for (int k = i == 0 && marker is not null ? 1 : 0; k < ws.Count; k++)
                {
                    var w = ws[k];
                    string text = w.Text;
                    bool lastInLine = k == ws.Count - 1, lastLine = i == g.Count - 1;
                    bool hyphenJoin = lastInLine && !lastLine && text.EndsWith('-') && text.Length > 1
                        && g[i + 1].Words.Count > 0 && char.IsLower(g[i + 1].Words[0].Text[0]);
                    if (hyphenJoin) text = text[..^1];
                    else if (!(lastInLine && lastLine)) text += " ";
                    AddRun(runs, new TextRun(text, FontFamily(w.Font, w.Flags), Math.Round(w.Size * 2) / 2, w.Bold, w.Italic, w.Rgb));
                }
            }
            var box = g.Select(l => l.Box).Aggregate((a, b) => a.Union(b));
            double size = Median(g.Select(l => l.Size));
            int heading = marker is null && size >= bodySize * 1.6 && g.Count <= 3 ? 1 : marker is null && size >= bodySize * 1.22 && g.Count <= 3 ? 2 : 0;
            double lineSpacing = g.Count >= 2 ? Math.Clamp(Median(g.Zip(g.Skip(1), (a, b) => b.Baseline - a.Baseline)) / size, 0.9, 3) : 1.15;
            yield return new ParagraphBlock(box, runs, AlignOf(g, alignArea ?? region, pageWidth), heading, 0, Math.Round(lineSpacing, 2), Math.Max(0, box.X0 - region.X0), marker, markerOffset);
        }
    }

    private static readonly HashSet<string> Bullets = new() { "•", "-", "–", "—", "▪", "■", "●", "◦", "○", "∙", "·", "*", "➢", "➤", "✓", "✔", "", "", "" };

    /// <summary>İlk sözcük bir liste işareti mi: madde imi, "1." / "1)" / "(1)" / "a)" / "iv." gibi.</summary>
    public static bool IsListMarker(string w, bool hasText)
    {
        if (!hasText || w.Length == 0) return false;
        if (Bullets.Contains(w)) return true;
        if (w.Length > 5) return false;
        if (w.Length >= 2 && (w[^1] == '.' || w[^1] == ')'))
        {
            var core = w[..^1].TrimStart('(');
            if (core.Length is >= 1 and <= 3 && core.All(char.IsDigit)) return true;
            if (core.Length == 1 && char.IsLetter(core[0]) && w[^1] == ')') return true;
        }
        return false;
    }

    private static bool AllBold(Line l) => l.Words.All(w => w.Bold);
    private static bool EndsSentence(Line l) => l.Words.Count > 0 && l.Words[^1].Text is { Length: > 0 } t && ".:!?;".Contains(t[^1]);

    private static void AddRun(List<TextRun> runs, TextRun r)
    {
        if (runs.Count > 0)
        {
            var p = runs[^1];
            if (p.FontFamily == r.FontFamily && Math.Abs(p.Size - r.Size) < 0.6 && p.Bold == r.Bold && p.Italic == r.Italic && p.Rgb == r.Rgb)
            { runs[^1] = p with { Text = p.Text + r.Text }; return; }
        }
        runs.Add(r);
    }

    private static ParaAlign AlignOf(List<Line> g, Box region, double pageWidth)
    {
        double tol = Math.Max(3, g[0].Size * 0.6);
        if (g.Count >= 2)
        {
            var body = g.Take(g.Count - 1).ToList();
            bool leftOk = g.Max(l => l.Box.X0) - g.Min(l => l.Box.X0) < tol;
            bool rightOk = body.Count >= 1 && body.Max(l => l.Box.X1) - body.Min(l => l.Box.X1) < tol;
            bool centerOk = g.Max(l => l.Box.CenterX) - g.Min(l => l.Box.CenterX) < tol;
            // İki yana yaslı: sol kenarlar hizalı, son satır dışındakiler bölgenin sağ kenarına kadar uzanıyor.
            bool fullWidth = body.All(l => region.X1 - l.Box.X1 < tol * 1.5);
            if (leftOk && rightOk && (g.Count >= 3 || fullWidth)) return ParaAlign.Justify;
            if (leftOk) return ParaAlign.Left;
            if (centerOk) return ParaAlign.Center;
            if (g.Max(l => l.Box.X1) - g.Min(l => l.Box.X1) < tol) return ParaAlign.Right;
            return ParaAlign.Left;
        }
        var b = g[0].Box;
        double regionCenter = (region.X0 + region.X1) / 2;
        bool wide = region.X1 - region.X0 > pageWidth * 0.4;
        if (wide && Math.Abs(b.CenterX - regionCenter) < tol * 2 && b.X0 > region.X0 + tol * 3) return ParaAlign.Center;
        if (wide && Math.Abs(b.X1 - region.X1) < tol && b.X0 > region.X0 + (region.X1 - region.X0) * 0.4) return ParaAlign.Right;
        return ParaAlign.Left;
    }

    /// <summary>
    /// PDF yazı tipi adını Word'de kullanılacak aile adına çevirir. Bilinen aileler korunur; LibreOffice/LaTeX/Linux yazı tipleri
    /// (Liberation, Nimbus, CM, Latin Modern…) ve bilinmeyenler serif/sans/eş aralıklı sınıfına göre Times New Roman / Arial / Courier New olur
    /// (aksi hâlde Word eksik yazı tipini rastgele bir şeyle değiştirir).
    /// </summary>
    public static string? FontFamily(string? pdfName, int flags = 0)
    {
        if (pdfName is null) return null;
        var fam = TextLayout.ParseFontName(pdfName).Family;
        var key = FontMatching.Key(fam);
        if (key.Length == 0) return null;
        string? known = key switch
        {
            "arial" => "Arial", "timesnewroman" => "Times New Roman", "couriernew" => "Courier New", "calibri" => "Calibri",
            "cambria" => "Cambria", "verdana" => "Verdana", "tahoma" => "Tahoma", "georgia" => "Georgia", "segoeui" => "Segoe UI",
            "consolas" => "Consolas", "trebuchetms" or "trebuchet" => "Trebuchet MS", "comicsansms" => "Comic Sans MS", "impact" => "Impact",
            "palatinolinotype" => "Palatino Linotype", "bookantiqua" => "Book Antiqua", "garamond" => "Garamond", "candara" => "Candara",
            "corbel" => "Corbel", "constantia" => "Constantia",
            _ => null,
        };
        if (known is not null) return known;
        static bool Has(string n, params string[] parts) => parts.Any(n.Contains);
        bool mono = Has(key, "mono", "courier", "typewriter", "consolas", "lucidaconsole") || key.StartsWith("cmtt") || (flags & 1) != 0;
        if (mono) return "Courier New";
        bool sans = Has(key, "sans", "arial", "helvet", "roboto", "segoe", "gothic", "frutiger", "myriad", "univers", "lato", "montserrat", "opensans", "calibri", "inter")
            || key.StartsWith("cmss") || key.StartsWith("lmsans");
        if (sans && !key.Contains("serif")) return "Arial";
        bool serif = Has(key, "serif", "roman", "times", "minion", "garamond", "palatino", "bookman", "century", "charter", "georgia", "cambria", "stix", "utopia", "caslon", "baskerville", "nimbusrom")
            || key.StartsWith("cmr") || key.StartsWith("cmbx") || key.StartsWith("cmti") || key.StartsWith("lmroman") || (flags & 2) != 0;
        if (serif) return "Times New Roman";
        return (flags & 32) != 0 && (flags & 2) == 0 ? "Arial" : SplitCamel(fam);
    }

    private static string SplitCamel(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString().Replace("-", " ").Trim();
    }

    // ─────────────── Tablo doldurma ───────────────

    private static TableBlock FillTable(TableDetector.Grid g, List<Word> words)
    {
        var cells = new List<TableCell>();
        int rows = g.RowEdges.Count - 1;
        for (int r = 0; r < rows; r++)
        {
            int c = 0;
            while (c < g.ColEdges.Count - 1)
            {
                int span = g.Span(r, c);
                var cellBox = new Box(g.ColEdges[c], g.RowEdges[r], g.ColEdges[c + span], g.RowEdges[r + 1]);
                var inCell = words.Where(w => cellBox.Contains(w.Box.CenterX, w.Box.CenterY)).ToList();
                var paras = inCell.Count == 0 ? new List<ParagraphBlock>()
                    : BuildParagraphs(BuildLines(inCell), cellBox.Inflate(-2), inCell.Average(w => w.Size), cellBox.Width).ToList();
                cells.Add(new TableCell(r, c, span, paras));
                c += span;
            }
        }
        return new TableBlock(g.Box, g.ColEdges, g.RowEdges, cells, g.Borderless);
    }
}
