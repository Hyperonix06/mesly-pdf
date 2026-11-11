namespace MeslyPdfReader.Core;

/// <summary>
/// Bir sayfanın kullanıcı alanı (PDF noktası, sol-alt köken) ile ekran dikdörtgeni arasındaki dönüşüm.
/// PDFium'un görüntüleme matrisiyle aynıdır: sayfa kutusu (CropBox) ekrandaki dikdörtgene, sayfanın
/// /Rotate değeri ve görünüm döndürmesi birleştirilerek eşlenir.
/// </summary>
public readonly record struct PageGeometry(RectF Box, int Rotation)
{
    /// <summary>Kullanıcı alanındaki noktayı [0,1]×[0,1] ekran koordinatına (y aşağı) çevirir.</summary>
    public (double U, double V) ToUnit(double x, double y, int viewQuarterTurns = 0)
    {
        double W = Box.Width, H = Box.Height;
        return (((Rotation + viewQuarterTurns) % 4 + 4) % 4) switch
        {
            1 => ((y - Box.Bottom) / H, (x - Box.Left) / W),
            2 => ((Box.Right - x) / W, (y - Box.Bottom) / H),
            3 => ((Box.Top - y) / H, (Box.Right - x) / W),
            _ => ((x - Box.Left) / W, (Box.Top - y) / H),
        };
    }

    public (double X, double Y) FromUnit(double u, double v, int viewQuarterTurns = 0)
    {
        double W = Box.Width, H = Box.Height;
        return (((Rotation + viewQuarterTurns) % 4 + 4) % 4) switch
        {
            1 => (Box.Left + v * W, Box.Bottom + u * H),
            2 => (Box.Right - u * W, Box.Bottom + v * H),
            3 => (Box.Right - v * W, Box.Top - u * H),
            _ => (Box.Left + u * W, Box.Top - v * H),
        };
    }

    /// <summary>Kullanıcı alanındaki dikdörtgeni ekran dikdörtgenine (x, y, w, h) çevirir.</summary>
    public (double X, double Y, double W, double H) ToDevice(RectF r, double left, double top, double width, double height, int viewQuarterTurns = 0)
    {
        var (u1, v1) = ToUnit(r.Left, r.Bottom, viewQuarterTurns);
        var (u2, v2) = ToUnit(r.Right, r.Top, viewQuarterTurns);
        double x1 = left + Math.Min(u1, u2) * width, x2 = left + Math.Max(u1, u2) * width;
        double y1 = top + Math.Min(v1, v2) * height, y2 = top + Math.Max(v1, v2) * height;
        return (x1, y1, x2 - x1, y2 - y1);
    }
}
