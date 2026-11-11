using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

public class PageRangeTests
{
    [Theory]
    [InlineData("1-3, 5", 10, new[] { 0, 1, 2, 4 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2", 10, new[] { 0, 1 })]
    [InlineData("3-1", 10, new[] { 2, 1, 0 })]
    [InlineData("son", 4, new[] { 3 })]
    [InlineData("2-son", 4, new[] { 1, 2, 3 })]
    [InlineData("tek", 5, new[] { 0, 2, 4 })]
    [InlineData("çift", 5, new[] { 1, 3 })]
    [InlineData("TÜMÜ", 3, new[] { 0, 1, 2 })]
    [InlineData("", 2, new[] { 0, 1 })]
    [InlineData("1,1,2", 3, new[] { 0, 1 })]
    public void Parses(string text, int count, int[] expected) => Assert.Equal(expected, PageRange.Parse(text, count));

    [Theory]
    [InlineData("0", 5)]
    [InlineData("6", 5)]
    [InlineData("abc", 5)]
    [InlineData("1-99", 5)]
    public void RejectsInvalid(string text, int count)
    {
        var ex = Assert.Throws<FormatException>(() => PageRange.Parse(text, count));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void FormatsCompactly() => Assert.Equal("1-3, 5, 7-8", PageRange.Format(new[] { 0, 1, 2, 4, 6, 7 }));
}

public class TurkishSearchTests
{
    [Theory]
    [InlineData("İSTANBUL ve Iğdır", "istanbul", 1)]
    [InlineData("istanbul", "İSTANBUL", 1)]
    [InlineData("IŞIK ışık", "ışık", 2)]
    [InlineData("ŞOFÖR şoför", "şoför", 2)]
    [InlineData("ÇĞÖÜ çğöü", "çğöü", 2)]
    public void CaseInsensitiveTurkish(string text, string q, int count)
        => Assert.Equal(count, TextSearch.FindAll(text, q, new SearchOptions()).Count());

    [Fact]
    public void TurkishDotlessIIsNotI()
    {
        // Türkçe kurallarında "ı" büyük harfi "I"dır; "i" ile eşleşmemelidir.
        Assert.Empty(TextSearch.FindAll("ışık", "isik", new SearchOptions()));
        Assert.Empty(TextSearch.FindAll("ILIK", "ilik", new SearchOptions()));
        Assert.Single(TextSearch.FindAll("ILIK", "ılık", new SearchOptions()));
    }

    [Fact]
    public void MatchCaseAndWholeWord()
    {
        var text = "Kara kara karakol";
        Assert.Equal(3, TextSearch.FindAll(text, "kara", new SearchOptions()).Count());
        Assert.Equal(2, TextSearch.FindAll(text, "kara", new SearchOptions { MatchCase = true }).Count());
        Assert.Equal(2, TextSearch.FindAll(text, "kara", new SearchOptions { WholeWord = true }).Count());
    }

    [Fact]
    public void IgnoreDiacriticsMatchesSCedilla()
        => Assert.Single(TextSearch.FindAll("Şoför", "sofor", new SearchOptions { IgnoreDiacritics = true }));
}

public class PageOrderTests
{
    [Theory]
    [InlineData(4, new[] { 3 }, 0, new[] { 3, 0, 1, 2 })]
    [InlineData(4, new[] { 0 }, 4, new[] { 1, 2, 3, 0 })]
    [InlineData(5, new[] { 1, 3 }, 0, new[] { 1, 3, 0, 2, 4 })]
    [InlineData(5, new[] { 0, 1 }, 3, new[] { 2, 0, 1, 3, 4 })]
    [InlineData(3, new[] { 1 }, 1, new[] { 0, 1, 2 })]
    public void ComputesMove(int count, int[] sel, int before, int[] expected)
        => Assert.Equal(expected, PageOrder.ComputeMove(count, sel, before));

    [Fact]
    public void SplitPlans()
    {
        Assert.Equal(3, PdfTools.PlanSplit(7, new SplitPlan(SplitMode.EveryNPages, 3)).Count);
        Assert.Equal(new[] { 6 }, PdfTools.PlanSplit(7, new SplitPlan(SplitMode.EveryNPages, 3))[2]);
        Assert.Equal(7, PdfTools.PlanSplit(7, new SplitPlan(SplitMode.SinglePages)).Count);
        var oe = PdfTools.PlanSplit(5, new SplitPlan(SplitMode.OddEven));
        Assert.Equal(new[] { 0, 2, 4 }, oe[0]); Assert.Equal(new[] { 1, 3 }, oe[1]);
        var r = PdfTools.PlanSplit(10, new SplitPlan(SplitMode.ByRanges, Ranges: "1-3; 4-6; 7-"));
        Assert.Equal(3, r.Count); Assert.Equal(new[] { 6, 7, 8, 9 }, r[2]);
    }

    [Fact]
    public void PaperSizeDescription()
    {
        Assert.Equal("A4", PaperSize.Describe(PaperSize.A4.ToPoints()));
        Assert.Equal("A4 yatay", PaperSize.Describe(PaperSize.A4.ToPoints().Swap()));
        Assert.Equal("A5", PaperSize.Describe(PaperSize.A5.ToPoints()));
    }
}
