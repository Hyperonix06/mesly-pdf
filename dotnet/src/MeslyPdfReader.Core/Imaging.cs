namespace MeslyPdfReader.Core;

/// <summary>8 bit gri görüntü (0 = siyah, 255 = beyaz).</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public GrayImage(int w, int h, byte[]? data = null) { Width = w; Height = h; Data = data ?? new byte[w * h]; }
    public byte this[int x, int y] { get => Data[y * Width + x]; set => Data[y * Width + x] = value; }
}

/// <summary>Tarama/görüntü düzeltme seçenekleri.</summary>
public sealed record ImageCorrection(bool AutoRotate = true, bool Deskew = true, bool CleanBorders = true, bool WhitenBackground = false)
{
    public static ImageCorrection None { get; } = new(false, false, false, false);
    public bool Any => AutoRotate || Deskew || CleanBorders || WhitenBackground;
}

/// <summary>Uygulanan düzeltmelerin raporu (kullanıcıya gösterilir).</summary>
public sealed record CorrectionReport(int QuarterTurnsApplied, double DeskewDegrees, bool BordersCleaned, bool BackgroundWhitened, string? Note = null)
{
    public bool Changed => QuarterTurnsApplied != 0 || Math.Abs(DeskewDegrees) > 0.01 || BordersCleaned || BackgroundWhitened;

    public string Describe()
    {
        var parts = new List<string>();
        if (QuarterTurnsApplied != 0) parts.Add($"{QuarterTurnsApplied * 90}° döndürüldü");
        if (Math.Abs(DeskewDegrees) > 0.01) parts.Add($"eğiklik {DeskewDegrees:+0.0;-0.0}° düzeltildi");
        if (BordersCleaned) parts.Add("kenar gölgeleri temizlendi");
        if (BackgroundWhitened) parts.Add("arka plan beyazlatıldı");
        return parts.Count == 0 ? "düzeltme gerekmedi" : string.Join(", ", parts);
    }
}

/// <summary>
/// Yönetilen görüntü işlemleri: griye çevirme, Otsu eşiği, eğiklik ölçümü (izdüşüm profili), döndürme,
/// kenar gölgesi ve arka plan temizleme. Yön (90/180/270°) algılaması OCR motorunda yapılır.
/// </summary>
public static class ImageOps
{
    public static GrayImage ToGray(BgraImage img)
    {
        var g = new GrayImage(img.Width, img.Height);
        var p = img.Pixels;
        for (int y = 0; y < img.Height; y++)
        {
            int row = y * img.Stride, o = y * img.Width;
            for (int x = 0; x < img.Width; x++)
            {
                int i = row + x * 4;
                g.Data[o + x] = (byte)((p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8);
            }
        }
        return g;
    }

    /// <summary>En uzun kenarı en fazla <paramref name="maxSide"/> olacak şekilde kutu ortalamasıyla küçültür.</summary>
    public static GrayImage Downscale(GrayImage g, int maxSide, out double factor)
    {
        factor = Math.Max(1, (double)Math.Max(g.Width, g.Height) / maxSide);
        int f = (int)Math.Ceiling(factor);
        if (f <= 1) { factor = 1; return g; }
        factor = f;
        int w = g.Width / f, h = g.Height / f;
        var r = new GrayImage(Math.Max(1, w), Math.Max(1, h));
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < f; dy++)
                {
                    int o = (y * f + dy) * g.Width + x * f;
                    for (int dx = 0; dx < f; dx++) sum += g.Data[o + dx];
                }
                r.Data[y * r.Width + x] = (byte)(sum / (f * f));
            }
        return r;
    }

    public static int OtsuThreshold(GrayImage g)
    {
        var hist = new long[256];
        foreach (var v in g.Data) hist[v]++;
        long total = g.Data.Length;
        double sum = 0; for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1; long wB = 0; int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = t; }
        }
        return thr;
    }

    /// <summary>
    /// Metin satırlarının eğikliğini derece olarak ölçer (pozitif: içerik saat yönünün tersine eğik).
    /// Satır izdüşümü profilinin keskinliğini en büyükleyen açı aranır. Metin yoksa 0 döner.
    /// </summary>
    public static double EstimateSkew(GrayImage g, double maxDegrees = 8)
    {
        var s = Downscale(g, 1400, out _);
        int thr = Math.Min(OtsuThreshold(s), 200);
        var xs = new List<int>(); var ys = new List<int>();
        // Kenarlardaki tarayıcı gölgesini ölçüme katma (%3 pay).
        int mx = s.Width * 3 / 100, my = s.Height * 3 / 100;
        for (int y = my; y < s.Height - my; y++)
            for (int x = mx; x < s.Width - mx; x++)
                if (s.Data[y * s.Width + x] < thr) { xs.Add(x); ys.Add(y); }
        int n = xs.Count;
        if (n < 200 || n > s.Width * s.Height * 0.5) return 0;
        // Çok yoğun görüntüde örnekle.
        int step = Math.Max(1, n / 60000);
        double Score(double deg)
        {
            double t = Math.Tan(deg * Math.PI / 180);
            int off = (int)Math.Ceiling(Math.Abs(t) * s.Width) + 2;
            var bins = new int[s.Height + 2 * off];
            for (int i = 0; i < n; i += step)
            {
                int yy = (int)Math.Round(ys[i] - xs[i] * t) + off;
                if ((uint)yy < (uint)bins.Length) bins[yy]++;
            }
            double sc = 0;
            for (int i = 1; i < bins.Length; i++) { double d = bins[i] - bins[i - 1]; sc += d * d; }
            return sc;
        }
        double bestA = 0, bestS = Score(0);
        for (double a = -maxDegrees; a <= maxDegrees + 1e-9; a += 0.25)
        {
            double sc = Score(a);
            if (sc > bestS) { bestS = sc; bestA = a; }
        }
        double center = bestA;
        for (double a = center - 0.3; a <= center + 0.3 + 1e-9; a += 0.02)
        {
            double sc = Score(a);
            if (sc > bestS) { bestS = sc; bestA = a; }
        }
        // Belirgin bir tepe yoksa (düz, metinsiz görüntü) düzeltme yapma.
        double baseS = Score(bestA + 3);
        if (bestS < baseS * 1.08) return 0;
        // Görüntü koordinatında y aşağı doğrudur: y' = y - x·tan(a). Satırlar sağa doğru aşağı iniyorsa a > 0.
        return Math.Round(bestA, 2);
    }

    /// <summary>Görüntüyü merkez etrafında döndürür (derece, pozitif = saat yönü), boşluklar beyazla doldurulur. Boyut korunur.</summary>
    public static BgraImage Rotate(BgraImage src, double degrees)
    {
        if (Math.Abs(degrees) < 0.005) return src;
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        int w = src.Width, h = src.Height, stride = w * 4;
        var dst = new byte[stride * h];
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        var sp = src.Pixels; int ss = src.Stride;
        Parallel.For(0, h, y =>
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                // Hedef → kaynak (ters döndürme).
                double dx = x - cx, dy = y - cy;
                double sx = cos * dx + sin * dy + cx, sy = -sin * dx + cos * dy + cy;
                int o = row + x * 4;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1) { dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 255; continue; }
                double fx = sx - x0, fy = sy - y0;
                int i00 = y0 * ss + x0 * 4, i10 = i00 + 4, i01 = i00 + ss, i11 = i01 + 4;
                for (int c = 0; c < 4; c++)
                {
                    double v = sp[i00 + c] * (1 - fx) * (1 - fy) + sp[i10 + c] * fx * (1 - fy) + sp[i01 + c] * (1 - fx) * fy + sp[i11 + c] * fx * fy;
                    dst[o + c] = (byte)Math.Clamp(v + 0.5, 0, 255);
                }
            }
        });
        return new BgraImage { Width = w, Height = h, Stride = stride, Pixels = dst };
    }

    /// <summary>90° katlarıyla saat yönünde döndürür.</summary>
    public static BgraImage RotateQuarter(BgraImage src, int quarterTurns)
    {
        int q = ((quarterTurns % 4) + 4) % 4;
        if (q == 0) return src;
        int w = src.Width, h = src.Height;
        int nw = q % 2 == 1 ? h : w, nh = q % 2 == 1 ? w : h, ns = nw * 4;
        var dst = new byte[ns * nh];
        var sp = src.Pixels; int ss = src.Stride;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int nx, ny;
                switch (q)
                {
                    case 1: nx = h - 1 - y; ny = x; break;
                    case 2: nx = w - 1 - x; ny = h - 1 - y; break;
                    default: nx = y; ny = w - 1 - x; break;
                }
                Buffer.BlockCopy(sp, y * ss + x * 4, dst, ny * ns + nx * 4, 4);
            }
        });
        return new BgraImage { Width = nw, Height = nh, Stride = ns, Pixels = dst };
    }

    /// <summary>
    /// Tarayıcı kapağı/kenarından kalan koyu şeritleri beyaza boyar. Bir kenardan başlayıp ortalaması koyu olan
    /// satır/sütunlar (en fazla %8) temizlenir. Temizlendiyse true.
    /// </summary>
    public static bool CleanBorders(BgraImage img, double maxFraction = 0.08)
    {
        var g = ToGray(img);
        int thr = 110;
        double RowMean(int y) { long s = 0; for (int x = 0; x < g.Width; x++) s += g.Data[y * g.Width + x]; return (double)s / g.Width; }
        double ColMean(int x) { long s = 0; for (int y = 0; y < g.Height; y++) s += g.Data[y * g.Width + x]; return (double)s / g.Height; }
        int maxR = (int)(g.Height * maxFraction), maxC = (int)(g.Width * maxFraction);
        int top = 0; while (top < maxR && RowMean(top) < thr) top++;
        int bottom = 0; while (bottom < maxR && RowMean(g.Height - 1 - bottom) < thr) bottom++;
        int left = 0; while (left < maxC && ColMean(left) < thr) left++;
        int right = 0; while (right < maxC && ColMean(g.Width - 1 - right) < thr) right++;
        if (top + bottom + left + right == 0) return false;
        // Gölge yumuşak biter: birkaç piksel pay ekle.
        int pad = Math.Max(2, Math.Min(g.Width, g.Height) / 300);
        void Fill(int x0, int y0, int x1, int y1)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(img.Height, y1); y++)
                Array.Fill(img.Pixels, (byte)255, y * img.Stride + Math.Max(0, x0) * 4, (Math.Min(img.Width, x1) - Math.Max(0, x0)) * 4);
        }
        if (top > 0) Fill(0, 0, img.Width, top + pad);
        if (bottom > 0) Fill(0, img.Height - bottom - pad, img.Width, img.Height);
        if (left > 0) Fill(0, 0, left + pad, img.Height);
        if (right > 0) Fill(img.Width - right - pad, 0, img.Width, img.Height);
        return true;
    }

    /// <summary>Kâğıt rengini (açık tonların %90 yüzdeliği) beyaza çeker; mürekkep korunur. Uygulandıysa true.</summary>
    public static bool WhitenBackground(BgraImage img)
    {
        var g = ToGray(img);
        var hist = new long[256];
        foreach (var v in g.Data) hist[v]++;
        long target = (long)(g.Data.Length * 0.6), acc = 0;
        int paper = 255;
        for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= g.Data.Length - target) { paper = i; break; } }
        paper = Math.Max(paper, 120);
        if (paper >= 250) return false;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Clamp(i * 255.0 / paper, 0, 255);
        var p = img.Pixels;
        for (int y = 0; y < img.Height; y++)
        {
            int row = y * img.Stride;
            for (int x = 0; x < img.Width; x++) { int o = row + x * 4; p[o] = lut[p[o]]; p[o + 1] = lut[p[o + 1]]; p[o + 2] = lut[p[o + 2]]; }
        }
        return true;
    }

    /// <summary>Gri görüntüden BGRA.</summary>
    public static BgraImage FromGray(GrayImage g)
    {
        var px = new byte[g.Width * g.Height * 4];
        for (int i = 0; i < g.Data.Length; i++) { byte v = g.Data[i]; px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v; px[i * 4 + 3] = 255; }
        return new BgraImage { Width = g.Width, Height = g.Height, Stride = g.Width * 4, Pixels = px };
    }

    /// <summary>Dikdörtgeni verilen renkle doldurur (piksel koordinatı).</summary>
    public static void FillRect(BgraImage img, int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        x0 = Math.Clamp(x0, 0, img.Width); x1 = Math.Clamp(x1, 0, img.Width);
        y0 = Math.Clamp(y0, 0, img.Height); y1 = Math.Clamp(y1, 0, img.Height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { int o = y * img.Stride + x * 4; img.Pixels[o] = b; img.Pixels[o + 1] = g; img.Pixels[o + 2] = r; img.Pixels[o + 3] = 255; }
    }

    /// <summary>Bir bölgenin çevresindeki (kenar şeridi) baskın açık rengi tahmin eder — silinen yazının yerine konacak kâğıt rengi.</summary>
    public static (byte B, byte G, byte R) EstimatePaperColor(BgraImage img, int x0, int y0, int x1, int y1)
    {
        var samples = new List<(byte, byte, byte, int)>();
        int pad = Math.Max(2, (y1 - y0) / 4);
        void Take(int x, int y)
        {
            if ((uint)x >= (uint)img.Width || (uint)y >= (uint)img.Height) return;
            int o = y * img.Stride + x * 4;
            byte bb = img.Pixels[o], gg = img.Pixels[o + 1], rr = img.Pixels[o + 2];
            samples.Add((bb, gg, rr, bb + gg + rr));
        }
        for (int x = x0 - pad; x < x1 + pad; x += 2) { Take(x, y0 - pad); Take(x, y1 + pad); }
        for (int y = y0; y < y1; y += 2) { Take(x0 - pad, y); Take(x1 + pad, y); }
        if (samples.Count == 0) return (255, 255, 255);
        // En açık %50'nin ortalaması (yakındaki yazıları dışarıda bırakır).
        var light = samples.OrderByDescending(s => s.Item4).Take(Math.Max(1, samples.Count / 2)).ToList();
        return ((byte)light.Average(s => s.Item1), (byte)light.Average(s => s.Item2), (byte)light.Average(s => s.Item3));
    }
}
