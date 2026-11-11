using MeslyPdfReader.Core;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Çizgili tabloları sayfa görüntüsünden bulur: uzun yatay/dikey koyu çizgiler, kesişimlerine göre gruplanır;
/// her grup bir ızgaradır. Hücre birleştirme, iki satır arasındaki dikey çizginin olmamasından anlaşılır.
/// Hem metin PDF'lerde (vektör çizgiler çizilmiş görüntüde görünür) hem taranmış sayfalarda çalışır.
/// </summary>
public static class TableDetector
{
    public sealed record Seg(double Pos, double From, double To, double Thickness);

    public sealed class Grid
    {
        public required Box Box { get; init; }
        public required List<double> ColEdges { get; init; }
        public required List<double> RowEdges { get; init; }
        /// <summary>[satır, sütun sınırı] → o satırda sınırda dikey çizgi var mı.</summary>
        public required bool[,] HasVertical { get; init; }

        /// <summary>Hücrenin sağa doğru kaç sütunu kapladığı.</summary>
        public int Span(int row, int col)
        {
            int s = 1;
            while (col + s < ColEdges.Count - 1 && !HasVertical[row, col + s]) s++;
            return s;
        }
    }

    /// <summary><paramref name="scale"/>: piksel → pt çarpanı.</summary>
    public static List<Grid> Detect(GrayImage g, double scale)
    {
        int w = g.Width, h = g.Height;
        int thr = Math.Min(170, ImageOps.OtsuThreshold(g) + 10);
        int minLen = Math.Max(18, Math.Min(w, h) / 30);
        var hs = Segments(g, thr, minLen, horizontal: true);
        var vs = Segments(g, thr, minLen, horizontal: false);
        if (hs.Count < 2 || vs.Count < 2) return new List<Grid>();

        // Kesişen çizgileri birleşim-bul ile grupla.
        int n = hs.Count + vs.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        double tol = 3;
        for (int i = 0; i < hs.Count; i++)
            for (int j = 0; j < vs.Count; j++)
            {
                var a = hs[i]; var b = vs[j];
                if (b.Pos >= a.From - tol && b.Pos <= a.To + tol && a.Pos >= b.From - tol && a.Pos <= b.To + tol)
                    parent[Find(i)] = Find(hs.Count + j);
            }
        var grids = new List<Grid>();
        foreach (var comp in Enumerable.Range(0, n).GroupBy(Find))
        {
            var ch = comp.Where(i => i < hs.Count).Select(i => hs[i]).ToList();
            var cv = comp.Where(i => i >= hs.Count).Select(i => vs[i - hs.Count]).ToList();
            if (ch.Count < 2 || cv.Count < 2) continue;
            var rows = Cluster(ch.Select(s => s.Pos));
            var cols = Cluster(cv.Select(s => s.Pos));
            if (rows.Count < 2 || cols.Count < 2) continue;
            if ((rows.Count - 1) * (cols.Count - 1) < 2) continue; // tek kutu: çerçeve, tablo değil
            // Satır yüksekliği metin satırından küçükse (ör. çift çizgi) birleştir.
            var hasV = new bool[rows.Count - 1, cols.Count];
            for (int r = 0; r < rows.Count - 1; r++)
            {
                double y0 = rows[r], y1 = rows[r + 1], need = (y1 - y0) * 0.6;
                for (int c = 0; c < cols.Count; c++)
                    hasV[r, c] = cv.Where(s => Math.Abs(s.Pos - cols[c]) <= tol + 1)
                        .Sum(s => Math.Max(0, Math.Min(s.To, y1) - Math.Max(s.From, y0))) >= need;
            }
            grids.Add(new Grid
            {
                Box = new Box(cols[0] * scale, rows[0] * scale, cols[^1] * scale, rows[^1] * scale),
                ColEdges = cols.Select(x => x * scale).ToList(),
                RowEdges = rows.Select(y => y * scale).ToList(),
                HasVertical = hasV,
            });
        }
        return grids;
    }

    private static List<double> Cluster(IEnumerable<double> xs, double tol = 4)
    {
        var res = new List<double>();
        foreach (var x in xs.OrderBy(v => v))
        {
            if (res.Count > 0 && x - res[^1] <= tol) res[^1] = (res[^1] + x) / 2;
            else res.Add(x);
        }
        return res;
    }

    /// <summary>Uzun koyu koşuları bulur, ardışık satırlardakileri (çizgi kalınlığı) birleştirir; kalın dolgu alanları atılır.</summary>
    private static List<Seg> Segments(GrayImage g, int thr, int minLen, bool horizontal)
    {
        int outer = horizontal ? g.Height : g.Width, inner = horizontal ? g.Width : g.Height;
        var raw = new List<(int Pos, int From, int To)>();
        for (int p = 0; p < outer; p++)
        {
            int run = -1;
            for (int q = 0; q <= inner; q++)
            {
                bool dark = q < inner && (horizontal ? g.Data[p * g.Width + q] : g.Data[q * g.Width + p]) < thr;
                if (dark) { if (run < 0) run = q; }
                else if (run >= 0)
                {
                    // Kısa kesintilere (kesik çizgi, tarama gürültüsü) izin ver: 2 px boşluk birleştirilir.
                    if (q - run >= minLen) raw.Add((p, run, q - 1));
                    run = -1;
                }
            }
        }
        // Kalınlık birleştirme
        var segs = new List<Seg>();
        var open = new List<(int P0, int P1, int From, int To)>();
        foreach (var grp in raw.GroupBy(r => r.Pos).OrderBy(x => x.Key))
        {
            var next = new List<(int, int, int, int)>();
            var used = new bool[open.Count];
            foreach (var r in grp)
            {
                int k = open.FindIndex(o => o.P1 == r.Pos - 1 && Math.Min(o.To, r.To) - Math.Max(o.From, r.From) > Math.Min(o.To - o.From, r.To - r.From) * 0.7);
                if (k >= 0 && !used[k]) { var o = open[k]; used[k] = true; next.Add((o.P0, r.Pos, Math.Min(o.From, r.From), Math.Max(o.To, r.To))); }
                else next.Add((r.Pos, r.Pos, r.From, r.To));
            }
            for (int i = 0; i < open.Count; i++) if (!used[i]) Close(open[i]);
            open = next;
        }
        foreach (var o in open) Close(o);
        return segs;

        void Close((int P0, int P1, int From, int To) o)
        {
            int thick = o.P1 - o.P0 + 1;
            if (thick > 6) return; // dolgu alanı / görsel, çizgi değil
            segs.Add(new Seg((o.P0 + o.P1) / 2.0, o.From, o.To, thick));
        }
    }
}
