namespace MeslyPdfReader.Core;

/// <summary>Görüntülenen sayfa koordinatında kutu: pt, sol-üst başlangıç, y aşağı doğru.</summary>
public readonly record struct Box(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public Box Union(Box o) => IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public bool Intersects(Box o) => X0 < o.X1 && X1 > o.X0 && Y0 < o.Y1 && Y1 > o.Y0;
    public Box Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);
}

/// <summary>Biçim bilgili karakter. <see cref="Baseline"/>: taban çizgisinin y değeri.</summary>
public sealed record StyledChar(char Char, Box Box, double Baseline, double Size, string? FontName, bool Bold, bool Italic, uint Rgb, bool Invisible, float Angle);

/// <summary>Sayfadaki bir görsel (piksellerle).</summary>
public sealed record PageImage(int ObjectIndex, Box Box, BgraImage Pixels, bool IsJpeg);

// ─────────────── Belge düzeni (Word/Excel dönüştürme modeli) ───────────────

public sealed record TextRun(string Text, string? FontFamily, double Size, bool Bold, bool Italic, uint Rgb);

public enum ParaAlign { Left, Center, Right, Justify }

public abstract record LayoutBlock(Box Box);

/// <summary>Paragraf: satırlar birleştirilmiş, biçimli parçalar halinde.</summary>
public sealed record ParagraphBlock(Box Box, IReadOnlyList<TextRun> Runs, ParaAlign Align, int HeadingLevel, double SpaceBefore, double LineSpacing, double Indent) : LayoutBlock(Box)
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

public sealed record TableCell(int Row, int Col, int ColSpan, IReadOnlyList<ParagraphBlock> Content)
{
    public string Text => string.Join("\n", Content.Select(p => p.Text));
}

/// <summary>Çizgili tablo. Sütun/satır sınırları pt cinsinden.</summary>
public sealed record TableBlock(Box Box, IReadOnlyList<double> ColEdges, IReadOnlyList<double> RowEdges, IReadOnlyList<TableCell> Cells) : LayoutBlock(Box)
{
    public int Rows => RowEdges.Count - 1;
    public int Cols => ColEdges.Count - 1;
}

public sealed record ImageBlock(Box Box, BgraImage Image, bool PreferJpeg) : LayoutBlock(Box);

/// <summary>Bir sayfanın okuma sırasındaki blokları.</summary>
public sealed record PageLayout(int PageIndex, double Width, double Height, IReadOnlyList<LayoutBlock> Blocks, bool FromOcr)
{
    public Box TextArea => Blocks.Select(b => b.Box).DefaultIfEmpty(new Box(54, 54, Width - 54, Height - 54)).Aggregate((a, b) => a.Union(b));
}
