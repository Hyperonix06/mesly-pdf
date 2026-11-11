namespace MeslyPdfReader.Core;

/// <summary>PDF noktası (1/72 inç) cinsinden boyut.</summary>
public readonly record struct SizeF(double Width, double Height)
{
    public bool IsLandscape => Width > Height;
    public SizeF Swap() => new(Height, Width);
    public override string ToString() => $"{Width:0.#} × {Height:0.#} pt";
}

/// <summary>PDF koordinatlarında (sol-alt köken) dikdörtgen.</summary>
public readonly record struct RectF(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;
    public RectF Union(RectF o) => IsEmpty ? o : o.IsEmpty ? this :
        new(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));
    public RectF Normalize() => new(Math.Min(Left, Right), Math.Min(Bottom, Top), Math.Max(Left, Right), Math.Max(Bottom, Top));
}

/// <summary>Sayfadaki tek bir karakter ve kutusu (sayfa koordinatları).</summary>
public readonly record struct TextChar(char Value, RectF Box, bool IsGenerated);

/// <summary>Bir sayfanın metin katmanı: karakterler ve indekslerine karşılık gelen kutular.</summary>
public sealed class PageText
{
    public required int PageIndex { get; init; }
    public required string Text { get; init; }
    /// <summary>Text[i] karakterinin kutusu. Kutusu olmayan (üretilmiş boşluk/satır sonu) karakterlerde boştur.</summary>
    public required RectF[] Boxes { get; init; }
    public int Length => Text.Length;

    /// <summary>Verilen noktaya en yakın karakterin indeksi (metin yoksa -1). <paramref name="exact"/> true ise yalnızca kutunun içindeyse.</summary>
    public int HitTest(double x, double y, bool exact = false)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < Boxes.Length; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            double dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
            double dy = y < b.Bottom ? b.Bottom - y : y > b.Top ? y - b.Top : 0;
            double d = dx * dx + dy * dy * 4; // dikey uzaklık daha önemli: satır seçimi
            if (d < bestD) { bestD = d; best = i; if (d == 0) break; }
        }
        return exact && bestD > 1 ? -1 : best;
    }

    /// <summary>[start, end] aralığındaki karakter kutularını satır bazında birleştirir.</summary>
    public IReadOnlyList<RectF> GetLineRects(int start, int end)
    {
        var list = new List<RectF>();
        if (Boxes.Length == 0) return list;
        if (start > end) (start, end) = (end, start);
        start = Math.Clamp(start, 0, Boxes.Length - 1);
        end = Math.Clamp(end, 0, Boxes.Length - 1);
        RectF cur = default; bool has = false;
        for (int i = start; i <= end; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            if (has)
            {
                double overlap = Math.Min(cur.Top, b.Top) - Math.Max(cur.Bottom, b.Bottom);
                double minH = Math.Min(cur.Height, b.Height);
                bool sameLine = overlap > minH * 0.5 && b.Left >= cur.Left - minH;
                if (sameLine) { cur = cur.Union(b); continue; }
                list.Add(cur);
            }
            cur = b; has = true;
        }
        if (has) list.Add(cur);
        return list;
    }
}

public sealed class Bookmark
{
    public string Title { get; set; } = "";
    public int PageIndex { get; set; } = -1;
    public string? Uri { get; set; }
    public List<Bookmark> Children { get; } = new();

    public Bookmark Clone()
    {
        var b = new Bookmark { Title = Title, PageIndex = PageIndex, Uri = Uri };
        foreach (var c in Children) b.Children.Add(c.Clone());
        return b;
    }

    public static int Count(IEnumerable<Bookmark> items) => items.Sum(b => 1 + Count(b.Children));
}

public sealed record LinkInfo(RectF Rect, int TargetPage, string? Uri);

public sealed record SearchHit(int PageIndex, int CharIndex, int Length, IReadOnlyList<RectF> Rects, string Context);

public sealed class SearchOptions
{
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }
    public bool IgnoreDiacritics { get; init; }
}

public sealed class DocumentInfo
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
    public string? CreationDate { get; init; }
    public string? ModDate { get; init; }
    public int PdfVersion { get; init; }
    public int PageCount { get; init; }
    public bool IsEncrypted { get; init; }
    public uint Permissions { get; init; }
    public long FileSize { get; init; }

    public string VersionText => PdfVersion > 0 ? $"PDF {PdfVersion / 10}.{PdfVersion % 10}" : "—";
}

public enum ImageFormatKind { Png, Jpeg, Tiff, Bmp }

/// <summary>BGRA (premultiplied değil) 32-bit görüntü tamponu.</summary>
public sealed class BgraImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required byte[] Pixels { get; init; }
    public long ByteSize => Pixels.LongLength;
}
