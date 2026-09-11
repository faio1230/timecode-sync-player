using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class CanvasPlacementTests
{
    private const double Tolerance = 1e-9;
    private static readonly FitRegistry Registry = FitRegistry.CreateDefault();

    private static Placement Compute(string fitId, int srcW, int srcH, int canvasW = 1920, int canvasH = 1080)
    {
        Registry.TryGet(fitId, out var calculator).Should().BeTrue();
        var placement = calculator.Compute(srcW, srcH, canvasW, canvasH);
        var d = placement.Destination;
        var c = placement.SourceCrop;
        d.X.Should().BeGreaterThanOrEqualTo(-Tolerance);
        d.Y.Should().BeGreaterThanOrEqualTo(-Tolerance);
        d.Right.Should().BeLessThanOrEqualTo(canvasW + Tolerance);
        d.Bottom.Should().BeLessThanOrEqualTo(canvasH + Tolerance);
        c.X.Should().BeGreaterThanOrEqualTo(-Tolerance);
        c.Y.Should().BeGreaterThanOrEqualTo(-Tolerance);
        c.Right.Should().BeLessThanOrEqualTo(srcW + Tolerance);
        c.Bottom.Should().BeLessThanOrEqualTo(srcH + Tolerance);
        Math.Abs(d.Width / c.Width - d.Height / c.Height).Should().BeLessThanOrEqualTo(Tolerance);
        Math.Abs(d.X + d.Width / 2 - canvasW / 2.0).Should().BeLessThanOrEqualTo(Tolerance);
        Math.Abs(d.Y + d.Height / 2 - canvasH / 2.0).Should().BeLessThanOrEqualTo(Tolerance);
        Math.Abs(c.X + c.Width / 2 - srcW / 2.0).Should().BeLessThanOrEqualTo(Tolerance);
        Math.Abs(c.Y + c.Height / 2 - srcH / 2.0).Should().BeLessThanOrEqualTo(Tolerance);
        return placement;
    }

    private static void Expect(Placement p, (double X, double Y, double W, double H) destination, (double X, double Y, double W, double H) crop)
    {
        p.Destination.X.Should().BeApproximately(destination.X, Tolerance);
        p.Destination.Y.Should().BeApproximately(destination.Y, Tolerance);
        p.Destination.Width.Should().BeApproximately(destination.W, Tolerance);
        p.Destination.Height.Should().BeApproximately(destination.H, Tolerance);
        p.SourceCrop.X.Should().BeApproximately(crop.X, Tolerance);
        p.SourceCrop.Y.Should().BeApproximately(crop.Y, Tolerance);
        p.SourceCrop.Width.Should().BeApproximately(crop.W, Tolerance);
        p.SourceCrop.Height.Should().BeApproximately(crop.H, Tolerance);
    }

    [Fact]
    public void Identity_SameAspectFitsExactlyForBothModes()
    {
        foreach (string id in new[] { FitHeight.FitId, FitWidth.FitId })
        {
            Expect(Compute(id, 1920, 1080), (0, 0, 1920, 1080), (0, 0, 1920, 1080));
            Expect(Compute(id, 3840, 2160), (0, 0, 1920, 1080), (0, 0, 3840, 2160));
            Expect(Compute(id, 960, 540), (0, 0, 1920, 1080), (0, 0, 960, 540));
        }
    }

    [Fact]
    public void FourByThree_BarsOrCropsDependingOnFitMode()
    {
        Expect(Compute(FitHeight.FitId, 1024, 768), (240, 0, 1440, 1080), (0, 0, 1024, 768));
        Expect(Compute(FitWidth.FitId, 1024, 768), (0, 0, 1920, 1080), (0, 96, 1024, 576));
    }

    [Fact]
    public void TwentyOneByNine_CropsOrBarsDependingOnFitMode()
    {
        Expect(Compute(FitHeight.FitId, 2560, 1080), (0, 0, 1920, 1080), (320, 0, 1920, 1080));
        Expect(Compute(FitWidth.FitId, 2560, 1080), (0, 135, 1920, 810), (0, 0, 2560, 1080));
    }

    [Fact]
    public void Portrait_HandlesRotatedSourcesAndCanvas()
    {
        Expect(Compute(FitHeight.FitId, 1080, 1920), (656.25, 0, 607.5, 1080), (0, 0, 1080, 1920));
        Expect(Compute(FitWidth.FitId, 1080, 1920), (0, 0, 1920, 1080), (0, 656.25, 1080, 607.5));
        Expect(Compute(FitWidth.FitId, 1920, 1080, 1080, 1920), (0, 656.25, 1080, 607.5), (0, 0, 1920, 1080));
        Expect(Compute(FitHeight.FitId, 1920, 1080, 1080, 1920), (0, 0, 1080, 1920), (656.25, 0, 607.5, 1080));
    }

    [Fact]
    public void OnePixel_ProducesSubPixelCropOrLargeBars()
    {
        Expect(Compute(FitHeight.FitId, 1, 1), (420, 0, 1080, 1080), (0, 0, 1, 1));
        Expect(Compute(FitWidth.FitId, 1, 1), (0, 0, 1920, 1080), (0, 0.21875, 1, 0.5625));
        Expect(Compute(FitHeight.FitId, 1, 1, 1, 1), (0, 0, 1, 1), (0, 0, 1, 1));
    }

    [Fact]
    public void LargerThanCanvas_UsesCropOrBars()
    {
        Expect(Compute(FitHeight.FitId, 3840, 1600), (0, 0, 1920, 1080), (497.77777777777777, 0, 2844.4444444444443, 1600));
        Expect(Compute(FitWidth.FitId, 3840, 1600), (0, 140, 1920, 800), (0, 0, 3840, 1600));
        Expect(Compute(FitHeight.FitId, 8192, 6144), (240, 0, 1440, 1080), (0, 0, 8192, 6144));
        Expect(Compute(FitWidth.FitId, 8192, 6144), (0, 0, 1920, 1080), (0, 768, 8192, 4608));
    }

    [Fact]
    public void FitRegistry_InheritsNullAndFallsBackWithWarning()
    {
        var canvas = new CanvasSettings(1920, 1080, FitHeight.FitId);
        Registry.Resolve(new ClipPlacement(null), canvas, out var warning).Id.Should().Be(FitHeight.FitId);
        warning.Should().BeNull();
        Registry.Resolve(new ClipPlacement("stretch"), canvas, out warning).Id.Should().Be(FitHeight.FitId);
        warning.Should().NotBeNull();
        warning.Should().Contain("stretch");
        Registry.Resolve(new ClipPlacement(FitWidth.FitId), canvas, out warning).Id.Should().Be(FitWidth.FitId);
        warning.Should().BeNull();
        Registry.Resolve(new ClipPlacement(null), new CanvasSettings(1920, 1080, FitWidth.FitId), out _).Id.Should().Be(FitWidth.FitId);
        FluentActions.Invoking(() => Registry.Resolve(new ClipPlacement(null), new CanvasSettings(1920, 1080, "missing"), out _))
            .Should().Throw<InvalidOperationException>();
        Expect(Registry.Compute(new ClipPlacement("unknown"), canvas, 2560, 1080, out string fitId, out warning),
            (0, 0, 1920, 1080), (320, 0, 1920, 1080));
        fitId.Should().Be(FitHeight.FitId);
        warning.Should().NotBeNull();
        CanvasSettings.Default.Should().Be(new CanvasSettings(1920, 1080, FitHeight.FitId));
        Registry.Ids.OrderBy(i => i, StringComparer.Ordinal).Should().Equal(FitHeight.FitId, FitWidth.FitId);
        FluentActions.Invoking(() => FitRegistry.CreateDefault().Register(new FitHeight())).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void InvalidSizes_Throw()
    {
        foreach (var calculator in new IFitCalculator[] { new FitHeight(), new FitWidth() })
            foreach (var (w, h, cw, ch) in new[] { (0, 1080, 1920, 1080), (1920, 0, 1920, 1080), (-1, 1080, 1920, 1080), (1920, 1080, 0, 1080), (1920, 1080, 1920, 0), (1920, 1080, 1920, -5) })
                FluentActions.Invoking(() => calculator.Compute(w, h, cw, ch)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new CanvasSettings(0, 1080, FitHeight.FitId)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new CanvasSettings(1920, -1, FitHeight.FitId)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new CanvasSettings(1920, 1080, "")).Should().Throw<ArgumentException>();
    }
}
