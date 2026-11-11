namespace MeslyPdfReader.Core;

public sealed record PaperSize(string Name, double WidthMm, double HeightMm)
{
    public const double PointsPerMm = 72.0 / 25.4;
    public SizeF ToPoints() => new(WidthMm * PointsPerMm, HeightMm * PointsPerMm);
    public override string ToString() => $"{Name} ({WidthMm:0.#} × {HeightMm:0.#} mm)";

    public static readonly PaperSize A3 = new("A3", 297, 420);
    public static readonly PaperSize A4 = new("A4", 210, 297);
    public static readonly PaperSize A5 = new("A5", 148, 210);
    public static readonly PaperSize Letter = new("Letter", 215.9, 279.4);
    public static readonly PaperSize Legal = new("Legal", 215.9, 355.6);

    public static IReadOnlyList<PaperSize> Standard { get; } = new[] { A4, A3, A5, Letter, Legal };

    public static double MmToPt(double mm) => mm * PointsPerMm;
    public static double PtToMm(double pt) => pt / PointsPerMm;

    /// <summary>Boyuta en yakın standart kâğıt adını verir (±3 mm, yön fark etmez).</summary>
    public static string Describe(SizeF pt)
    {
        double w = PtToMm(pt.Width), h = PtToMm(pt.Height);
        foreach (var p in Standard)
        {
            if ((Math.Abs(p.WidthMm - w) < 3 && Math.Abs(p.HeightMm - h) < 3) ||
                (Math.Abs(p.WidthMm - h) < 3 && Math.Abs(p.HeightMm - w) < 3))
                return p.Name + (w > h ? " yatay" : "");
        }
        return $"{w:0} × {h:0} mm";
    }
}
