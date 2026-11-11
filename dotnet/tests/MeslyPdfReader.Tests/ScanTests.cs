using MeslyPdfReader.Core;
using Xunit;

namespace MeslyPdfReader.Tests;

/// <summary>Sahte tarayıcı: besleyicide belirli sayıda kâğıt, istenirse belirli sayfada sıkışma.</summary>
internal sealed class FakeScanner : IScannerBackend
{
    private readonly int _sheets, _jamAt;
    private readonly ScanErrorKind? _beginError;
    private int _taken;
    public ScanSettings? Settings { get; private set; }
    public bool Ended { get; private set; }
    public Action? OnAcquire { get; set; }

    public FakeScanner(int sheets, int jamAt = -1, ScanErrorKind? beginError = null) { _sheets = sheets; _jamAt = jamAt; _beginError = beginError; }

    public void Begin(string deviceId, ScanSettings settings)
    {
        if (_beginError is { } k) throw new ScanException(k);
        Settings = settings;
    }

    public ScannedPage AcquireNext()
    {
        OnAcquire?.Invoke();
        if (_taken == _jamAt) throw new ScanException(ScanErrorKind.PaperJam);
        bool duplex = Settings!.Source == ScanSource.FeederDuplex;
        int available = Settings.Source == ScanSource.Flatbed ? int.MaxValue : _sheets * (duplex ? 2 : 1);
        if (_taken >= available) throw new ScanException(ScanErrorKind.PaperEmpty);
        _taken++;
        return new ScannedPage { Data = new byte[] { (byte)_taken }, Format = ScannedFormat.Jpeg, Dpi = Settings.Dpi };
    }

    public void End() => Ended = true;
}

public class ScanTests
{
    private static ScanSettings S(ScanSource src) => new(src, ScanColorMode.Color, 300, PaperSize.A4);

    [Fact]
    public void FlatbedScansExactlyOnePage()
    {
        var f = new FakeScanner(0);
        var r = ScanJob.Run(f, "x", S(ScanSource.Flatbed));
        Assert.Single(r.Pages);
        Assert.Null(r.StoppedBy);
        Assert.True(f.Ended);
    }

    [Fact]
    public void FeederScansUntilEmpty()
    {
        var seen = new List<int>();
        var r = ScanJob.Run(new FakeScanner(5), "x", S(ScanSource.Feeder), p => seen.Add(p.Data[0]));
        Assert.Equal(5, r.Pages.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, seen);
        Assert.Null(r.StoppedBy);
    }

    [Fact]
    public void DuplexGivesTwoSidesPerSheet()
        => Assert.Equal(6, ScanJob.Run(new FakeScanner(3), "x", S(ScanSource.FeederDuplex)).Pages.Count);

    [Fact]
    public void EmptyFeederAtStartIsAnError()
    {
        var f = new FakeScanner(0);
        var ex = Assert.Throws<ScanException>(() => ScanJob.Run(f, "x", S(ScanSource.Feeder)));
        Assert.Equal(ScanErrorKind.PaperEmpty, ex.Kind);
        Assert.Contains("besleyicide kâğıt yok", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(f.Ended);
    }

    [Fact]
    public void JamKeepsAlreadyScannedPages()
    {
        var r = ScanJob.Run(new FakeScanner(10, jamAt: 3), "x", S(ScanSource.Feeder));
        Assert.Equal(3, r.Pages.Count);
        Assert.True(r.Partial);
        Assert.Equal(ScanErrorKind.PaperJam, r.StoppedBy!.Kind);
    }

    [Fact]
    public void CancellationStopsBetweenPagesAndKeepsPages()
    {
        using var cts = new CancellationTokenSource();
        var f = new FakeScanner(10);
        int n = 0;
        var r = ScanJob.Run(f, "x", S(ScanSource.Feeder), _ => { if (++n == 2) cts.Cancel(); }, cts.Token);
        Assert.Equal(2, r.Pages.Count);
        Assert.Equal(ScanErrorKind.Cancelled, r.StoppedBy!.Kind);
        Assert.True(f.Ended);
    }

    [Fact]
    public void DeviceErrorsHaveTurkishMessages()
    {
        var ex = Assert.Throws<ScanException>(() => ScanJob.Run(new FakeScanner(1, beginError: ScanErrorKind.Busy), "x", S(ScanSource.Feeder)));
        Assert.Contains("kullanılıyor", ex.Message);
        foreach (ScanErrorKind k in Enum.GetValues<ScanErrorKind>()) Assert.False(string.IsNullOrWhiteSpace(ScanException.Describe(k)));
    }

    [Theory]
    [InlineData(300, 2480, 3508)]   // A4 @300 dpi
    [InlineData(150, 1240, 1754)]
    public void PaperToPixels(int dpi, int w, int h)
    {
        var (pw, ph) = ScanJob.ExtentPixels(PaperSize.A4, dpi, 0, 0);
        Assert.InRange(pw, w - 1, w + 1);
        Assert.InRange(ph, h - 1, h + 1);
    }

    [Fact]
    public void PaperClampedToScannerBed()
    {
        // Letter genişliğinde bir yatak (8.5") A4 yüksekliğini sınırlamalı değil ama Legal'i kısaltmalı.
        var (w, h) = ScanJob.ExtentPixels(PaperSize.Legal, 300, 2550, 3508);
        Assert.Equal(2550, w);
        Assert.Equal(3508, h);
        Assert.Equal((2550, 3508), ScanJob.ExtentPixels(null, 300, 2550, 3508));
    }
}
