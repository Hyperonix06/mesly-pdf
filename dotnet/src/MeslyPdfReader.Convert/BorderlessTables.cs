using MeslyPdfReader.Core;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Çizgisiz (yalnızca hizalamayla oluşan) tabloları sözcük konumlarından bulur. Sözcükler satırlara, satırlar geniş
/// boşluklarla hücre parçalarına bölünür; ardışık ≥3 satırda aynı yerde boş kalan dikey şeritler (oluk) sütunları verir.
/// Düz yazı ve iki sütunlu metin, hücrelerinin uzunluğu nedeniyle (tablo hücreleri kısadır) tablo sayılmaz.
/// Sonuç, çizgili tablolarla aynı <see cref="TableDetector.Grid"/> biçimindedir (Borderless = true).
/// </summary>
internal static class BorderlessTables
{
    private sealed record Seg(Box Box, List<LayoutAnalyzer.Word> Words);
    private sealed record Row(double Baseline, double Size, Box Box, List<Seg> Segs);

    public static List<TableDetector.Grid> Detect(List<LayoutAnalyzer.Word> words)
    {
        var result = new List<TableDetector.Grid>();
        if (words.Count < 6) return result;
        var rows = BuildRows(words);
        var run = new List<Row>();
        void Flush()
        {
            if (run.Count >= 3 && TryGrid(run) is { } g) result.Add(g);
            run = new List<Row>();
        }
        foreach (var r in rows)
        {
            bool candidate = r.Segs.Count >= 2 || IsShortNumericRow(r);
            if (!candidate) { Flush(); continue; }
            if (run.Count > 0 && r.Baseline - run[^1].Baseline > 3.4 * Math.Max(r.Size, run[^1].Size)) Flush();
            run.Add(r);
        }
        Flush();
        return result;
    }

    /// <summary>Tek parça görünen ama kısa, sayı içeren ve belirgin sözcük aralığı olan satır (dar sütunlu sayısal tablo satırı).</summary>
    private static bool IsShortNumericRow(Row r)
    {
        var ws = r.Segs.SelectMany(x => x.Words).OrderBy(w => w.Box.X0).ToList();
        if (ws.Count < 3 || ws.Count > 8) return false;
        if (!ws.Skip(1).Any(w => w.Text.Any(char.IsDigit))) return false;
        for (int i = 1; i < ws.Count; i++)
            if (ws[i].Box.X0 - ws[i - 1].Box.X1 >= 0.45 * r.Size) return true;
        return false;
    }

    private static List<Row> BuildRows(List<LayoutAnalyzer.Word> words)
    {
        var groups = new List<List<LayoutAnalyzer.Word>>();
        foreach (var w in words.OrderBy(w => w.Baseline).ThenBy(w => w.Box.X0))
        {
            var g = groups.Count == 0 ? null : groups[^1];
            if (g is not null && Math.Abs(g.Average(x => x.Baseline) - w.Baseline) <= 0.4 * Math.Max(w.Size, g[0].Size)) g.Add(w);
            else groups.Add(new List<LayoutAnalyzer.Word> { w });
        }
        var rows = new List<Row>();
        foreach (var g in groups)
        {
            g.Sort((a, b) => a.Box.X0.CompareTo(b.Box.X0));
            double size = g.Select(w => w.Size).OrderBy(x => x).ElementAt(g.Count / 2);
            var segs = new List<Seg>();
            var cur = new List<LayoutAnalyzer.Word> { g[0] };
            for (int i = 1; i < g.Count; i++)
            {
                // Hücre arası boşluk: bir em'den belirgin geniş (sözcük arası ≈ 0,25–0,5 em).
                if (g[i].Box.X0 - cur.Max(w => w.Box.X1) >= 1.2 * size) { segs.Add(Make(cur)); cur = new List<LayoutAnalyzer.Word>(); }
                cur.Add(g[i]);
            }
            segs.Add(Make(cur));
            rows.Add(new Row(g.Average(w => w.Baseline), size, g.Select(w => w.Box).Aggregate((a, b) => a.Union(b)), segs));
        }
        return rows;

        static Seg Make(List<LayoutAnalyzer.Word> ws) => new(ws.Select(w => w.Box).Aggregate((a, b) => a.Union(b)), ws);
    }

    private static TableDetector.Grid? TryGrid(List<Row> run)
    {
        var segs = run.SelectMany(r => r.Segs).ToList();
        // Tablo hücreleri kısadır; uzun satırlar yazı sütunudur.
        var counts = segs.Select(s => s.Words.Count).OrderBy(x => x).ToList();
        if (counts[counts.Count / 2] > 5) return null;
        double size = run.Average(r => r.Size);
        double left = segs.Min(s => s.Box.X0), right = segs.Max(s => s.Box.X1);
        int nBins = (int)Math.Ceiling(right - left) + 1;
        if (nBins < 20) return null;

        // Her 1 pt'lik şeritte kaç satırın hücre parçası var.
        var cover = new int[nBins];
        foreach (var r in run)
            foreach (var w in r.Segs.SelectMany(s => s.Words))
                for (int x = (int)Math.Ceiling(w.Box.X0 - left); x <= (int)Math.Floor(w.Box.X1 - left) && x < nBins; x++) cover[Math.Max(0, x)]++;
        int tolerance = (int)Math.Floor(run.Count * 0.15); // birkaç satır oluğu aşan geniş hücre (başlık/ara toplam) olabilir
        double minGutter = Math.Max(3.5, 0.3 * size);

        // Olukları bul: tolerans altında kalan şeritler (uçlar hariç).
        var gutters = new List<(double From, double To)>();
        int start = -1;
        for (int x = 0; x <= nBins; x++)
        {
            bool free = x < nBins && cover[x] <= tolerance;
            if (free) { if (start < 0) start = x; }
            else if (start >= 0)
            {
                if (start > 0 && x < nBins && x - start >= minGutter) gutters.Add((left + start, left + x));
                start = -1;
            }
        }
        if (gutters.Count < 1 || !gutters.Any(g => g.To - g.From >= Math.Max(4, 0.4 * size))) return null; // en az bir belirgin oluk şart

        var colEdges = new List<double> { left - 2 };
        colEdges.AddRange(gutters.Select(g => (g.From + g.To) / 2));
        colEdges.Add(right + 2);
        int cols = colEdges.Count - 1;
        if (cols < 2) return null;

        // Her sütun satırların çoğunda dolu olmalı (aksi hâlde rastlantısal hizalama).
        for (int c = 0; c < cols; c++)
        {
            int filled = run.Count(r => r.Segs.SelectMany(s => s.Words).Any(w => w.Box.CenterX >= colEdges[c] && w.Box.CenterX < colEdges[c + 1]));
            if (filled < run.Count * 0.5) return null;
        }
        // İki sütunlu yazıyı ele: en geniş hücre sütun genişliğine yakınsa ve hücreler çok sözcüklüyse zaten yukarıda elendi;
        // ayrıca tüm hücrelerin ortalama sözcük sayısı yüksekse yazı sayılır.
        if (counts.Average() > 4.5) return null;

        var rowEdges = new List<double> { run[0].Box.Y0 - 2 };
        for (int i = 1; i < run.Count; i++) rowEdges.Add((run[i - 1].Box.Y1 + run[i].Box.Y0) / 2);
        rowEdges.Add(run[^1].Box.Y1 + 2);

        var hasV = new bool[run.Count, colEdges.Count];
        for (int r = 0; r < run.Count; r++)
            for (int c = 0; c < colEdges.Count; c++)
            {
                bool crossed = c > 0 && c < colEdges.Count - 1 && run[r].Segs.SelectMany(s => s.Words).Any(w => w.Box.X0 < colEdges[c] - 1 && w.Box.X1 > colEdges[c] + 1);
                hasV[r, c] = !crossed;
            }
        return new TableDetector.Grid
        {
            Box = new Box(colEdges[0], rowEdges[0], colEdges[^1], rowEdges[^1]),
            ColEdges = colEdges, RowEdges = rowEdges, HasVertical = hasV, Borderless = true,
        };
    }
}
