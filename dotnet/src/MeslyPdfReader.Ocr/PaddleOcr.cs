using System.Text;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.Ocr;

/// <summary>
/// Gelişmiş OCR motoru: PaddleOCR PP-OCRv6 (Apache-2.0) tespit + tanıma modelleri, ONNX Runtime ile bu bilgisayarda çalışır.
/// Tesseract'a göre bozuk taramalarda ve Türkçe harflerde (ı, İ, ş, ğ, ç, ö, ü) daha doğrudur; tanıma sözcük kutuları CTC zaman adımlarından çıkarılır.
/// </summary>
public static class PaddleOcr
{
    private const int DetMaxSide = 2000, DetMinSide = 736, RecHeight = 48, RecBaseWidth = 320, RecBatch = 6;
    private const float DetThreshold = 0.3f, BoxThreshold = 0.5f, UnclipRatio = 1.6f, MinTextScore = 0.5f;

    private static readonly object Gate = new();
    private static Ort.Model? _det, _rec;
    private static string[]? _chars;
    private static string? _loadError;
    private static bool _loaded;

    /// <summary>Model klasörü: MESLY_PADDLE_MODELS ortam değişkeni veya uygulama klasöründeki ocr\paddle.</summary>
    public static string? FindModelDir()
    {
        foreach (var d in new[] { Environment.GetEnvironmentVariable("MESLY_PADDLE_MODELS"), Path.Combine(AppContext.BaseDirectory, "ocr", "paddle") })
            if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d!, "det.onnx")) && File.Exists(Path.Combine(d!, "rec.onnx"))) return d;
        return null;
    }

    /// <summary>Kullanılamıyorsa Türkçe neden, kullanılabiliyorsa null. Modeller ilk kullanımda belleğe alınır.</summary>
    public static string? Unavailable()
    {
        lock (Gate)
        {
            if (_loaded) return _loadError;
            if (FindModelDir() is not { } dir) return "Gelişmiş OCR modelleri bulunamadı.";
            if (Ort.EnsureLoaded() is { } why) return why;
            try
            {
                int threads = Math.Clamp(Environment.ProcessorCount, 1, 8);
                var rec = Ort.Model.Load(Path.Combine(dir, "rec.onnx"), threads);
                if (!rec.Metadata.TryGetValue("character", out var dict) || string.IsNullOrEmpty(dict))
                    throw new OcrException("Tanıma modelinde karakter sözlüğü yok.");
                var list = dict.Split('\n').ToList();
                _chars = new[] { "" }.Concat(list).Concat(new[] { " " }).ToArray(); // 0 = boşluk (CTC blank), sonda ' '
                _det = Ort.Model.Load(Path.Combine(dir, "det.onnx"), threads);
                _rec = rec;
                _loaded = true;
                return _loadError = null;
            }
            catch (Exception ex)
            {
                _loaded = true;
                return _loadError = "Gelişmiş OCR başlatılamadı: " + ex.Message;
            }
        }
    }

    public static string Version() => "PP-OCRv6 (small) · ONNX Runtime 1.22";

    // ─────────────── Tespit sonucu ───────────────

    private readonly record struct Pt(double X, double Y);
    private sealed record TextBox(Pt Tl, Pt Tr, Pt Br, Pt Bl, float Score);

    /// <summary>Gri görüntüyü tanır. <paramref name="progress"/> 0–1.</summary>
    public static OcrPage Recognize(GrayImage img, int dpi, string languages, CancellationToken ct = default, Action<double>? progress = null)
    {
        if (Unavailable() is { } why) throw new OcrException(why);
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(0.02);
        var boxes = Detect(img, ct);
        progress?.Invoke(0.3);
        if (boxes.Count == 0) return new OcrPage(img.Width, img.Height, dpi, Array.Empty<OcrWord>(), languages);

        // Her kutudan tanıma girdisi (yükseklik 48'e ölçekli gri satır).
        var crops = new List<Crop>(boxes.Count);
        foreach (var b in boxes) crops.Add(MakeCrop(img, b));
        var order = Enumerable.Range(0, crops.Count).OrderBy(i => (double)crops[i].W / crops[i].H).ToArray();
        var lines = new LineResult?[crops.Count];
        for (int s = 0; s < order.Length; s += RecBatch)
        {
            ct.ThrowIfCancellationRequested();
            var idx = order.Skip(s).Take(RecBatch).ToArray();
            RecognizeBatch(crops, idx, lines);
            progress?.Invoke(0.3 + 0.68 * Math.Min(order.Length, s + RecBatch) / order.Length);
        }

        var items = new List<Ln>();
        for (int i = 0; i < crops.Count; i++)
        {
            if (lines[i] is not { } lr || lr.Score < MinTextScore || lr.Words.Count == 0) continue;
            var ln = BuildLine(crops[i], lr, img.Width, img.Height);
            if (ln is not null) items.Add(ln);
        }
        var words = ReadingOrder(items);
        progress?.Invoke(1);
        return new OcrPage(img.Width, img.Height, dpi, words, languages);
    }

    // ─────────────── Tespit (DB) ───────────────

    private static List<TextBox> Detect(GrayImage img, CancellationToken ct)
    {
        int W = img.Width, H = img.Height;
        double ratio = Math.Max(W, H) > DetMaxSide ? DetMaxSide / (double)Math.Max(W, H) : 1.0;
        if (Math.Min(W, H) * ratio < DetMinSide) ratio = DetMinSide / (double)Math.Min(W, H);
        int dw = Math.Max(32, (int)Math.Round(W * ratio / 32.0) * 32), dh = Math.Max(32, (int)Math.Round(H * ratio / 32.0) * 32);
        if ((long)dw * dh > 4200L * 4200L) { double f = Math.Sqrt(4200.0 * 4200.0 / ((double)dw * dh)); dw = Math.Max(32, (int)(dw * f / 32) * 32); dh = Math.Max(32, (int)(dh * f / 32) * 32); }

        var plane = new float[dw * dh];
        ResizeToNormalized(img, plane, dw, dh);
        var input = new float[3 * dw * dh];
        for (int c = 0; c < 3; c++) Array.Copy(plane, 0, input, c * plane.Length, plane.Length);
        ct.ThrowIfCancellationRequested();
        var (prob, shape) = _det!.Run(input, new long[] { 1, 3, dh, dw });
        int ph = (int)shape[^2], pw = (int)shape[^1];
        ct.ThrowIfCancellationRequested();
        return DbPostProcess(prob, pw, ph, W, H);
    }

    /// <summary>cv2.resize(INTER_LINEAR) ile aynı yarım piksel merkezli çift doğrusal örnekleme; ardından (x/255-0.5)/0.5.</summary>
    private static void ResizeToNormalized(GrayImage src, float[] dst, int dw, int dh)
    {
        int sw = src.Width, sh = src.Height;
        double fx = (double)sw / dw, fy = (double)sh / dh;
        var data = src.Data;
        for (int y = 0; y < dh; y++)
        {
            double sy = (y + 0.5) * fy - 0.5;
            int y0 = (int)Math.Floor(sy); double wy = sy - y0;
            int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                double sx = (x + 0.5) * fx - 0.5;
                int x0 = (int)Math.Floor(sx); double wx = sx - x0;
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                double v = (data[ya * sw + xa] * (1 - wx) + data[ya * sw + xb] * wx) * (1 - wy) + (data[yb * sw + xa] * (1 - wx) + data[yb * sw + xb] * wx) * wy;
                dst[y * dw + x] = (float)((v / 255.0 - 0.5) / 0.5);
            }
        }
    }

    private static List<TextBox> DbPostProcess(float[] prob, int pw, int ph, int destW, int destH)
    {
        // İkili harita + 2×2 genişletme (cv2.dilate, çapa merkezde: (y-1..y, x-1..x)).
        var mask = new bool[pw * ph];
        for (int i = 0; i < mask.Length; i++) mask[i] = prob[i] > DetThreshold;
        var dil = new bool[pw * ph];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                dil[y * pw + x] = mask[y * pw + x] || (x > 0 && mask[y * pw + x - 1]) || (y > 0 && mask[(y - 1) * pw + x]) || (x > 0 && y > 0 && mask[(y - 1) * pw + x - 1]);

        var seen = new bool[pw * ph];
        var stack = new Stack<int>();
        var result = new List<TextBox>();
        for (int start = 0; start < dil.Length; start++)
        {
            if (!dil[start] || seen[start]) continue;
            // 8 komşuluklu bağlı bileşen; her satırın en sol/sağ pikseli yeterli (dışbükey zarf için).
            int minY = start / pw, maxY = minY;
            var rows = new Dictionary<int, (int Min, int Max)>();
            stack.Push(start); seen[start] = true; int count = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); int y = p / pw, x = p - y * pw; count++;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                rows[y] = rows.TryGetValue(y, out var r) ? (Math.Min(r.Min, x), Math.Max(r.Max, x)) : (x, x);
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy; if (ny < 0 || ny >= ph) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx; if (nx < 0 || nx >= pw) continue;
                        int q = ny * pw + nx;
                        if (dil[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
                    }
                }
            }
            if (count < 4) continue;
            var pts = new List<Pt>(rows.Count * 2);
            foreach (var (y, r) in rows) { pts.Add(new Pt(r.Min, y)); if (r.Max != r.Min) pts.Add(new Pt(r.Max, y)); }
            var hull = ConvexHull(pts);
            var (corners, w, h) = MinAreaRect(hull);
            if (Math.Min(w, h) < 3) continue;
            float score = RectScore(prob, pw, ph, corners);
            if (score < BoxThreshold) continue;

            // unclip: poligon alanı·oran/çevre kadar her yöne genişlet (dikdörtgende kenarlar d kadar uzar).
            double d = w * h * UnclipRatio / (2 * (w + h));
            var (c2, w2, h2) = Expand(corners, w, h, d);
            if (Math.Min(w2, h2) < 5) continue;

            // Orijinal görüntü koordinatlarına; saat yönünde sırala (tl, tr, br, bl).
            var mapped = c2.Select(p => new Pt(Math.Clamp(Math.Round(p.X / pw * destW), 0, destW - 1), Math.Clamp(Math.Round(p.Y / ph * destH), 0, destH - 1))).ToArray();
            var o = OrderClockwise(mapped);
            double rw = Dist(o[0], o[1]), rh = Dist(o[0], o[3]);
            if ((int)rw <= 3 || (int)rh <= 3) continue;
            result.Add(new TextBox(o[0], o[1], o[2], o[3], score));
        }
        return result;
    }

    private static double Dist(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static List<Pt> ConvexHull(List<Pt> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<Pt>();
        foreach (var q in p) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(q); }
        var upper = new List<Pt>();
        for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(q); }
        lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>Dışbükey zarfın en küçük alanlı döndürülmüş dikdörtgeni (cv2.minAreaRect). Köşeler sırayla döner.</summary>
    private static (Pt[] Corners, double W, double H) MinAreaRect(List<Pt> hull)
    {
        if (hull.Count < 3)
        {
            if (hull.Count == 0) return (Array.Empty<Pt>(), 0, 0);
            var a = hull[0]; var b = hull[^1];
            return (new[] { a, b, b, a }, Dist(a, b), 0);
        }
        double bestArea = double.MaxValue; Pt[] best = Array.Empty<Pt>(); double bw = 0, bh = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var p = hull[i]; var q = hull[(i + 1) % hull.Count];
            double dx = q.X - p.X, dy = q.Y - p.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            dx /= len; dy /= len; double nx = -dy, ny = dx;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var h in hull)
            {
                double u = h.X * dx + h.Y * dy, v = h.X * nx + h.Y * ny;
                if (u < minU) minU = u; if (u > maxU) maxU = u;
                if (v < minV) minV = v; if (v > maxV) maxV = v;
            }
            double w = maxU - minU, hh = maxV - minV, area = w * hh;
            if (area < bestArea)
            {
                bestArea = area; bw = w; bh = hh;
                Pt At(double u, double v) => new(u * dx + v * nx, u * dy + v * ny);
                best = new[] { At(minU, minV), At(maxU, minV), At(maxU, maxV), At(minU, maxV) };
            }
        }
        return (best, bw, bh);
    }

    private static (Pt[] Corners, double W, double H) Expand(Pt[] c, double w, double h, double d)
    {
        if (c.Length != 4) return (c, w, h);
        var cx = c.Average(p => p.X); var cy = c.Average(p => p.Y);
        // c[0]→c[1] "w" yönü, c[1]→c[2] "h" yönü.
        double ux = (c[1].X - c[0].X) / Math.Max(w, 1e-9), uy = (c[1].Y - c[0].Y) / Math.Max(w, 1e-9);
        double vx = (c[3].X - c[0].X) / Math.Max(h, 1e-9), vy = (c[3].Y - c[0].Y) / Math.Max(h, 1e-9);
        double hw = w / 2 + d, hh = h / 2 + d;
        Pt P(double a, double b) => new(cx + a * ux + b * vx, cy + a * uy + b * vy);
        return (new[] { P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh) }, 2 * hw, 2 * hh);
    }

    private static float RectScore(float[] prob, int pw, int ph, Pt[] c)
    {
        int x0 = Math.Clamp((int)Math.Floor(c.Min(p => p.X)), 0, pw - 1), x1 = Math.Clamp((int)Math.Ceiling(c.Max(p => p.X)), 0, pw - 1);
        int y0 = Math.Clamp((int)Math.Floor(c.Min(p => p.Y)), 0, ph - 1), y1 = Math.Clamp((int)Math.Ceiling(c.Max(p => p.Y)), 0, ph - 1);
        double sum = 0; int n = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (InsideQuad(c, x, y)) { sum += prob[y * pw + x]; n++; }
        return n == 0 ? 0 : (float)(sum / n);
    }

    private static bool InsideQuad(Pt[] c, double x, double y)
    {
        int pos = 0, neg = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = c[i]; var b = c[(i + 1) % 4];
            double cr = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (cr > 1e-6) pos++; else if (cr < -1e-6) neg++;
        }
        return pos == 0 || neg == 0;
    }

    private static Pt[] OrderClockwise(Pt[] pts)
    {
        var xs = pts.OrderBy(p => p.X).ToArray();
        var left = xs.Take(2).OrderBy(p => p.Y).ToArray(); // tl, bl
        var right = xs.Skip(2).OrderBy(p => p.Y).ToArray(); // tr, br
        return new[] { left[0], right[0], right[1], left[1] };
    }

    // ─────────────── Satır kırpma ve tanıma ───────────────

    private sealed class Crop
    {
        public required TextBox Box { get; init; }
        public int W { get; init; }
        public int H { get; init; }
        public required byte[] Pixels { get; init; } // W×H gri (özgün çözünürlük)
    }

    private static Crop MakeCrop(GrayImage img, TextBox b)
    {
        int cw = Math.Max(1, (int)Math.Ceiling(Math.Max(Dist(b.Tl, b.Tr), Dist(b.Bl, b.Br))));
        int ch = Math.Max(1, (int)Math.Ceiling(Math.Max(Dist(b.Tl, b.Bl), Dist(b.Tr, b.Br))));
        var px = new byte[cw * ch];
        double ux = (b.Tr.X - b.Tl.X) / cw, uy = (b.Tr.Y - b.Tl.Y) / cw, vx = (b.Bl.X - b.Tl.X) / ch, vy = (b.Bl.Y - b.Tl.Y) / ch;
        int W = img.Width, H = img.Height; var d = img.Data;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                double sx = b.Tl.X + (x + 0.5) * ux + (y + 0.5) * vx - 0.5, sy = b.Tl.Y + (x + 0.5) * uy + (y + 0.5) * vy - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy); double wx = sx - x0, wy = sy - y0;
                int xa = Math.Clamp(x0, 0, W - 1), xb = Math.Clamp(x0 + 1, 0, W - 1), ya = Math.Clamp(y0, 0, H - 1), yb = Math.Clamp(y0 + 1, 0, H - 1);
                double v = (d[ya * W + xa] * (1 - wx) + d[ya * W + xb] * wx) * (1 - wy) + (d[yb * W + xa] * (1 - wx) + d[yb * W + xb] * wx) * wy;
                px[y * cw + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        return new Crop { Box = b, W = cw, H = ch, Pixels = px };
    }

    private sealed record CharPos(string Text, int Step, float Prob);
    private sealed record LineResult(List<CharPos> Chars, List<(string Text, int FirstChar, int LastChar)> Words, float Score, int Steps, int PaddedWidth, int ResizedWidth, int CropWidth)
    {
        public int Count => Chars.Count;
    }

    private static void RecognizeBatch(List<Crop> crops, int[] idx, LineResult?[] results)
    {
        double maxRatio = (double)RecBaseWidth / RecHeight;
        foreach (var i in idx) maxRatio = Math.Max(maxRatio, (double)crops[i].W / crops[i].H);
        int padW = (int)(RecHeight * maxRatio);
        var input = new float[idx.Length * 3 * RecHeight * padW];
        var resizedWidths = new int[idx.Length];
        for (int n = 0; n < idx.Length; n++)
        {
            var c = crops[idx[n]];
            double ratio = (double)c.W / c.H;
            int rw = Math.Min((int)Math.Ceiling(RecHeight * ratio), padW);
            rw = Math.Max(1, rw);
            resizedWidths[n] = rw;
            var plane = new float[RecHeight * padW]; // 0 = nötr gri dolgu
            var small = ResizeGray(c.Pixels, c.W, c.H, rw, RecHeight);
            for (int y = 0; y < RecHeight; y++)
                for (int x = 0; x < rw; x++)
                    plane[y * padW + x] = (small[y * rw + x] / 255f - 0.5f) / 0.5f;
            int baseOff = n * 3 * RecHeight * padW;
            for (int ch = 0; ch < 3; ch++) Array.Copy(plane, 0, input, baseOff + ch * plane.Length, plane.Length);
        }
        var (logits, shape) = _rec!.Run(input, new long[] { idx.Length, 3, RecHeight, padW });
        int T = (int)shape[1], C = (int)shape[2];
        for (int n = 0; n < idx.Length; n++)
            results[idx[n]] = Decode(logits, n * T * C, T, C, padW, resizedWidths[n], crops[idx[n]].W);
    }

    private static LineResult Decode(float[] logits, int offset, int T, int C, int padW, int resizedW, int cropW)
    {
        var chars = new List<CharPos>();
        int prev = -1; double probSum = 0;
        for (int t = 0; t < T; t++)
        {
            int best = 0; float bp = float.MinValue; int o = offset + t * C;
            for (int c = 0; c < C; c++) { float v = logits[o + c]; if (v > bp) { bp = v; best = c; } }
            if (best != 0 && best != prev && best < _chars!.Length)
            {
                chars.Add(new CharPos(_chars![best], t, bp));
                probSum += bp;
            }
            prev = best;
        }
        // Sözcükleri boşluk karakterinden ayır.
        var words = new List<(string, int, int)>();
        var sb = new StringBuilder(); int first = -1;
        for (int i = 0; i < chars.Count; i++)
        {
            if (chars[i].Text == " ")
            {
                if (sb.Length > 0) { words.Add((sb.ToString(), first, i - 1)); sb.Clear(); first = -1; }
                continue;
            }
            if (first < 0) first = i;
            sb.Append(chars[i].Text);
        }
        if (sb.Length > 0) words.Add((sb.ToString(), first, chars.Count - 1));
        float score = chars.Count == 0 ? 0 : (float)(probSum / chars.Count);
        return new LineResult(chars, words, score, T, padW, resizedW, cropW);
    }

    /// <summary>Gri görüntü yeniden boyutlama: büyütmede çift doğrusal, küçültmede alan ortalaması.</summary>
    private static byte[] ResizeGray(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        if (fx > 1 || fy > 1)
        {
            // Alan ortalaması (kutu süzgeci).
            for (int y = 0; y < dh; y++)
            {
                double y0 = y * fy, y1 = Math.Min(sh, (y + 1) * fy);
                for (int x = 0; x < dw; x++)
                {
                    double x0 = x * fx, x1 = Math.Min(sw, (x + 1) * fx);
                    double sum = 0, wsum = 0;
                    for (int yy = (int)y0; yy < Math.Ceiling(y1); yy++)
                    {
                        double wy = Math.Min(yy + 1, y1) - Math.Max(yy, y0);
                        for (int xx = (int)x0; xx < Math.Ceiling(x1); xx++)
                        {
                            double wx = Math.Min(xx + 1, x1) - Math.Max(xx, x0), w = wx * wy;
                            sum += src[Math.Min(yy, sh - 1) * sw + Math.Min(xx, sw - 1)] * w; wsum += w;
                        }
                    }
                    dst[y * dw + x] = (byte)Math.Clamp((int)Math.Round(wsum > 0 ? sum / wsum : 255), 0, 255);
                }
            }
            return dst;
        }
        for (int y = 0; y < dh; y++)
        {
            double sy = (y + 0.5) * fy - 0.5; int y0 = (int)Math.Floor(sy); double wy = sy - y0;
            int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dw; x++)
            {
                double sx = (x + 0.5) * fx - 0.5; int x0 = (int)Math.Floor(sx); double wx = sx - x0;
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                double v = (src[ya * sw + xa] * (1 - wx) + src[ya * sw + xb] * wx) * (1 - wy) + (src[yb * sw + xa] * (1 - wx) + src[yb * sw + xb] * wx) * wy;
                dst[y * dw + x] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
            }
        }
        return dst;
    }

    // ─────────────── Sözcük kutuları ve okuma sırası ───────────────

    private sealed class Ln
    {
        public required List<OcrWord> Words { get; init; }
        public int Left, Top, Right, Bottom;
        public int Height => Bottom - Top;
        public int CharCount;
    }

    private static Ln? BuildLine(Crop crop, LineResult lr, int imgW, int imgH)
    {
        var b = crop.Box;
        int cw = crop.W, chh = crop.H;
        double stepToCrop = (double)lr.PaddedWidth / lr.Steps * ((double)cw / lr.ResizedWidth); // bir CTC adımının kırpma pikseli
        // Her karakterin merkezi (kırpma x'i).
        var centers = lr.Chars.Select(c => (c.Step + 0.5) * stepToCrop).ToArray();
        double avgChar = lr.Count == 0 ? cw : (double)cw / Math.Max(1, lr.Chars.Count(c => c.Text != " "));

        Pt Map(double u, double v) => new(b.Tl.X + u / cw * (b.Tr.X - b.Tl.X) + v / chh * (b.Bl.X - b.Tl.X), b.Tl.Y + u / cw * (b.Tr.Y - b.Tl.Y) + v / chh * (b.Bl.Y - b.Tl.Y));

        // Taban çizgisi: kırpmadaki koyu piksel satır profilinden (iniş çizgileri taban çizgisinin altında kalır).
        double baseV = EstimateBaseline(crop);
        // Algılama kutusu (unclip yüzünden) mürekkepten yüksektir; yazı boyutu kutu yüksekliğinden çıkarıldığı için
        // sözcük kutusunu dikeyde gerçek mürekkep aralığına daraltırız (Tesseract kutularıyla aynı anlam).
        var words = new List<OcrWord>();
        for (int wi = 0; wi < lr.Words.Count; wi++)
        {
            var (text, f, l) = lr.Words[wi];
            double prevEdge = wi == 0 ? 0 : (centers[lr.Words[wi - 1].LastChar] + centers[f]) / 2;
            double nextEdge = wi == lr.Words.Count - 1 ? cw : (centers[l] + centers[lr.Words[wi + 1].FirstChar]) / 2;
            double left = Math.Max(prevEdge, centers[f] - avgChar * 0.6), right = Math.Min(nextEdge, centers[l] + avgChar * 0.6);
            if (right - left < 2) { left = Math.Max(0, centers[f] - 1); right = Math.Min(cw, centers[l] + 1); }
            left = Math.Max(0, left); right = Math.Min(cw, Math.Max(right, left + 1));
            // Sözcüğün kendi mürekkep aralığı: yazı boyutu sözcüğün en yüksek harfinden hesaplanır (Tesseract kutularıyla aynı anlam).
            var (inkTop, inkBottom) = EstimateInkRows(crop, (int)left, (int)Math.Ceiling(right));
            var corners = new[] { Map(left, inkTop), Map(right, inkTop), Map(right, inkBottom), Map(left, inkBottom) };
            int L = Math.Clamp((int)Math.Floor(corners.Min(p => p.X)), 0, imgW - 1), R = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X)), L + 1, imgW);
            int T = Math.Clamp((int)Math.Floor(corners.Min(p => p.Y)), 0, imgH - 1), B = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.Y)), T + 1, imgH);
            int baseY = (int)Math.Round(Map(left, baseV).Y);
            baseY = Math.Clamp(baseY, T, B);
            float conf = (float)(lr.Chars.Skip(f).Take(l - f + 1).Where(c => c.Text != " ").DefaultIfEmpty(new CharPos("", 0, 0)).Average(c => c.Prob) * 100.0);
            words.Add(new OcrWord(OcrEngine.Normalize(text), L, T, R, B, conf, 0, 0, baseY));
        }
        if (words.Count == 0) return null;
        return new Ln
        {
            Words = words,
            Left = words.Min(w => w.Left), Top = words.Min(w => w.Top), Right = words.Max(w => w.Right), Bottom = words.Max(w => w.Bottom),
            CharCount = words.Sum(w => w.Text.Length),
        };
    }

    /// <summary>Kırpmada mürekkebin ilk ve son satırı (gürültüyü yok sayar); mürekkep yoksa tüm yükseklik.</summary>
    private static (double Top, double Bottom) EstimateInkRows(Crop c, int x0, int x1)
    {
        int thr = Otsu(c.Pixels);
        x0 = Math.Clamp(x0, 0, c.W - 1); x1 = Math.Clamp(x1, x0 + 1, c.W);
        var rows = new int[c.H];
        for (int y = 0; y < c.H; y++)
        {
            int n = 0;
            for (int x = x0; x < x1; x++) if (c.Pixels[y * c.W + x] < thr) n++;
            rows[y] = n;
        }
        int max = rows.Max();
        if (max == 0) return (0, c.H);
        int cut = Math.Max(1, (int)(max * 0.03));
        int first = Array.FindIndex(rows, n => n >= cut), last = Array.FindLastIndex(rows, n => n >= cut);
        if (first < 0 || last < first) return (0, c.H);
        int pad = Math.Max(1, (int)Math.Round((last - first + 1) * 0.02)); // nokta/aksan ve iniş çizgisi payı
        return (Math.Max(0, first - pad), Math.Min(c.H, last + 1 + pad));
    }

    /// <summary>Kırpma içinde taban çizgisinin satır konumu (0..H): koyu piksel yoğunluğunun belirgin düştüğü son satır.</summary>
    private static double EstimateBaseline(Crop c)
    {
        int thr = Otsu(c.Pixels);
        var rows = new int[c.H];
        for (int y = 0; y < c.H; y++)
        {
            int n = 0;
            for (int x = 0; x < c.W; x++) if (c.Pixels[y * c.W + x] < thr) n++;
            rows[y] = n;
        }
        int max = rows.Max();
        if (max == 0) return c.H * 0.8;
        // En yoğun satırın %40'ının altına düşmeden önceki son satır = taban çizgisi.
        int peak = Array.IndexOf(rows, max), last = peak;
        for (int y = peak; y < c.H; y++) if (rows[y] >= max * 0.4) last = y;
        return Math.Min(c.H, last + 1);
    }

    private static int Otsu(byte[] px)
    {
        var hist = new int[256]; foreach (var v in px) hist[v]++;
        long total = px.Length, sum = 0; for (int i = 0; i < 256; i++) sum += i * (long)hist[i];
        long sumB = 0, wB = 0; double best = -1; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += t * (long)hist[t];
            double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = t; }
        }
        return thr;
    }

    /// <summary>Özyinelemeli XY-kesimi: yatay boşlukla bölümlere, geniş sütun boşluğuyla sütunlara ayırır; yaprak = blok.</summary>
    private static List<OcrWord> ReadingOrder(List<Ln> lines)
    {
        var result = new List<OcrWord>();
        if (lines.Count == 0) return result;
        double medianH = lines.Select(l => (double)l.Height).OrderBy(h => h).ElementAt(lines.Count / 2);
        var blocks = new List<List<Ln>>();
        Cut(lines, medianH, blocks);
        int lineNo = 0, blockNo = 0;
        foreach (var block in blocks)
        {
            foreach (var row in Rows(block))
            {
                // Aynı satırın parçalarının (ayrı algılanmış kutular) taban çizgisi 1-2 piksel oynayabilir; paragraf
                // algılama satır başına tek taban çizgisi bekler. Yakın olanlar ortancaya sabitlenir.
                var allBase = row.SelectMany(l => l.Words).Select(w => w.BaselineY).OrderBy(v => v).ToList();
                int rowBase = allBase[allBase.Count / 2];
                int tol = Math.Max(3, (int)(medianH * 0.25));
                foreach (var ln in row.OrderBy(l => l.Left))
                    foreach (var w in ln.Words)
                    {
                        int bl = Math.Abs(w.BaselineY - rowBase) <= tol ? rowBase : w.BaselineY;
                        // Taban çizgisi her zaman sözcük kutusunun içinde kalsın (yazı boyutu Top'tan hesaplanır, etkilenmez).
                        result.Add(w with { Line = lineNo, Block = blockNo, BaselineY = bl, Top = Math.Min(w.Top, bl), Bottom = Math.Max(w.Bottom, bl) });
                    }
                lineNo++;
            }
            blockNo++;
        }
        return result;
    }

    private static void Cut(List<Ln> set, double medianH, List<List<Ln>> blocks)
    {
        if (set.Count <= 1) { if (set.Count == 1) blocks.Add(set); return; }
        // Dikey kesim (önce: boşluklu paragraflar iki sütunu satır satır bölmesin): yalnız uzun metin satırlarından oluşan sütunlar (tablo hücreleri bölünmez).
        var byLeft = set.OrderBy(l => l.Left).ToList();
        int maxRight = byLeft[0].Right;
        double xGap = Math.Max(8, medianH * 1.5);
        for (int i = 1; i < byLeft.Count; i++)
        {
            if (byLeft[i].Left - maxRight >= xGap)
            {
                var a = byLeft.Take(i).ToList(); var b = byLeft.Skip(i).ToList();
                if (a.Count >= 3 && b.Count >= 3 && AvgChars(a) >= 25 && AvgChars(b) >= 25)
                {
                    Cut(a, medianH, blocks); Cut(b, medianH, blocks);
                    return;
                }
            }
            maxRight = Math.Max(maxRight, byLeft[i].Right);
        }
        // Yatay kesim: tüm satırların yatay izdüşümünde yeterli boşluk.
        var byTop = set.OrderBy(l => l.Top).ToList();
        int maxBottom = byTop[0].Bottom;
        double yGap = Math.Max(4, medianH * 0.7);
        for (int i = 1; i < byTop.Count; i++)
        {
            if (byTop[i].Top - maxBottom >= yGap)
            {
                Cut(byTop.Take(i).ToList(), medianH, blocks);
                Cut(byTop.Skip(i).ToList(), medianH, blocks);
                return;
            }
            maxBottom = Math.Max(maxBottom, byTop[i].Bottom);
        }
        blocks.Add(set);
    }

    private static double AvgChars(List<Ln> l) => l.Average(x => x.CharCount);

    private static IEnumerable<List<Ln>> Rows(List<Ln> block)
    {
        var rows = new List<List<Ln>>();
        foreach (var l in block.OrderBy(l => l.Top).ThenBy(l => l.Left))
        {
            var row = rows.Count == 0 ? null : rows[^1];
            if (row is not null)
            {
                int top = row.Min(x => x.Top), bottom = row.Max(x => x.Bottom);
                int overlap = Math.Min(bottom, l.Bottom) - Math.Max(top, l.Top);
                if (overlap >= 0.5 * Math.Min(bottom - top, l.Height)) { row.Add(l); continue; }
            }
            rows.Add(new List<Ln> { l });
        }
        return rows;
    }
}
