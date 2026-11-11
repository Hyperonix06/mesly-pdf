using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

public enum PrintScale { Fit, ShrinkOnly, ActualSize }
public enum PrintOrientation { Auto, Portrait, Landscape }

public sealed class PrintOptions
{
    public IReadOnlyList<int> Pages { get; set; } = Array.Empty<int>();
    /// <summary>Kâğıt boyutu, DIP (1/96 inç).</summary>
    public Size Paper { get; set; } = new(793.7, 1122.5);
    public Thickness Margins { get; set; } = new(12);
    public PrintScale Scale { get; set; } = PrintScale.Fit;
    public PrintOrientation Orientation { get; set; } = PrintOrientation.Auto;
    public bool Annotations { get; set; } = true;
    public bool Grayscale { get; set; }
    public int Dpi { get; set; } = 300;
}

/// <summary>PDF sayfalarını yazıcı çözünürlüğünde, istek üzerine çizen sayfalayıcı (tüm belge belleğe alınmaz).</summary>
public sealed class PdfPaginator : DocumentPaginator
{
    private readonly PdfDocument _doc;
    private readonly PrintOptions _o;

    public PdfPaginator(PdfDocument doc, PrintOptions options) { _doc = doc; _o = options; }

    public override bool IsPageCountValid => true;
    public override int PageCount => _o.Pages.Count;
    public override Size PageSize { get => _o.Paper; set => _o.Paper = value; }
    public override IDocumentPaginatorSource? Source => null;

    /// <summary>Bir sayfanın kâğıt üzerindeki yerleşimi (önizleme ile yazdırma aynı hesabı kullanır).</summary>
    public (Size Paper, Rect Image, int Turns) Layout(int n)
    {
        var size = _doc.GetPageSize(_o.Pages[n]);
        double pw = size.Width / 72 * 96, ph = size.Height / 72 * 96;
        var paper = _o.Paper;
        bool pageLandscape = pw > ph;
        bool paperLandscape = _o.Orientation switch
        {
            PrintOrientation.Landscape => true,
            PrintOrientation.Portrait => false,
            _ => pageLandscape,
        };
        if (paperLandscape != (paper.Width > paper.Height)) paper = new Size(paper.Height, paper.Width);
        int turns = 0;
        var area = new Rect(_o.Margins.Left, _o.Margins.Top, Math.Max(10, paper.Width - _o.Margins.Left - _o.Margins.Right), Math.Max(10, paper.Height - _o.Margins.Top - _o.Margins.Bottom));
        if (_o.Orientation != PrintOrientation.Auto && pageLandscape != paperLandscape) { turns = 1; (pw, ph) = (ph, pw); }
        double fit = Math.Min(area.Width / pw, area.Height / ph);
        double s = _o.Scale switch { PrintScale.ActualSize => 1, PrintScale.ShrinkOnly => Math.Min(1, fit), _ => fit };
        double w = pw * s, h = ph * s;
        return (paper, new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h), turns);
    }

    public ImageSource RenderPage(int n, double dpi)
    {
        var (_, rect, turns) = Layout(n);
        double scale = dpi / 96.0;
        int w = (int)Math.Round(rect.Width * scale), h = (int)Math.Round(rect.Height * scale);
        double cap = 7000.0 / Math.Max(w, h);
        if (cap < 1) { w = (int)(w * cap); h = (int)(h * cap); }
        var img = _doc.Render(_o.Pages[n], new RenderRequest(Math.Max(1, w), Math.Max(1, h), QuarterTurns: turns, Annotations: _o.Annotations, Printing: true, Grayscale: _o.Grayscale));
        return ImageCodec.ToBitmapSource(img, dpi);
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var (paper, rect, _) = Layout(pageNumber);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var clip = new Rect(0, 0, paper.Width, paper.Height);
            dc.PushClip(new RectangleGeometry(clip));
            dc.DrawImage(RenderPage(pageNumber, _o.Dpi), rect);
            dc.Pop();
        }
        var box = new Rect(paper);
        return new DocumentPage(visual, paper, box, box);
    }
}
