namespace GpuOutputProbe;

// Placement calculation (docs/CANVAS-PLACEMENT-SPEC.md) with fixed expected rectangles; no D3D object is created.
internal static class CanvasPlacementSelfTests
{
    private const double Tolerance = 1e-9;
    private static readonly FitRegistry Registry = FitRegistry.CreateDefault();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static bool Near(PlacementRect actual, double x, double y, double width, double height) =>
        Math.Abs(actual.X - x) <= Tolerance && Math.Abs(actual.Y - y) <= Tolerance && Math.Abs(actual.Width - width) <= Tolerance && Math.Abs(actual.Height - height) <= Tolerance;
    private static Placement Compute(string fitId, int srcW, int srcH, int canvasW = 1920, int canvasH = 1080)
    {
        Check(Registry.TryGet(fitId, out var calculator), "Fit id not registered: " + fitId);
        var placement = calculator.Compute(srcW, srcH, canvasW, canvasH);
        var d = placement.Destination; var c = placement.SourceCrop;
        Check(d.X >= -Tolerance && d.Y >= -Tolerance && d.Right <= canvasW + Tolerance && d.Bottom <= canvasH + Tolerance, "Destination left the canvas.");
        Check(c.X >= -Tolerance && c.Y >= -Tolerance && c.Right <= srcW + Tolerance && c.Bottom <= srcH + Tolerance, "Source crop left the source.");
        Check(Math.Abs(d.Width / c.Width - d.Height / c.Height) <= Tolerance, "Scale is not uniform.");
        Check(Math.Abs(d.X + d.Width / 2 - canvasW / 2.0) <= Tolerance && Math.Abs(d.Y + d.Height / 2 - canvasH / 2.0) <= Tolerance, "Destination is not centered.");
        Check(Math.Abs(c.X + c.Width / 2 - srcW / 2.0) <= Tolerance && Math.Abs(c.Y + c.Height / 2 - srcH / 2.0) <= Tolerance, "Crop is not centered.");
        return placement;
    }
    private static void Expect(Placement p, (double X, double Y, double W, double H) destination, (double X, double Y, double W, double H) crop, string name)
    {
        Check(Near(p.Destination, destination.X, destination.Y, destination.W, destination.H), name + ": destination " + p.Destination);
        Check(Near(p.SourceCrop, crop.X, crop.Y, crop.W, crop.H), name + ": crop " + p.SourceCrop);
    }

    public static void Identity()
    {
        foreach (string id in new[] { FitHeight.FitId, FitWidth.FitId })
        {
            Expect(Compute(id, 1920, 1080), (0, 0, 1920, 1080), (0, 0, 1920, 1080), id + " 16:9 same size");
            Expect(Compute(id, 3840, 2160), (0, 0, 1920, 1080), (0, 0, 3840, 2160), id + " 16:9 larger source"); // Uniform 0.5, no crop, no bars.
            Expect(Compute(id, 960, 540), (0, 0, 1920, 1080), (0, 0, 960, 540), id + " 16:9 smaller source");
        }
    }

    public static void FourByThree()
    {
        // fit-height: scale 1080/768 = 1.40625, width 1440 -> bars 240 each side, full source.
        Expect(Compute(FitHeight.FitId, 1024, 768), (240, 0, 1440, 1080), (0, 0, 1024, 768), "4:3 fit-height");
        // fit-width: scale 1920/1024 = 1.875, height 1440 -> crop to 1080/1.875 = 576 source rows, 96 off top and bottom.
        Expect(Compute(FitWidth.FitId, 1024, 768), (0, 0, 1920, 1080), (0, 96, 1024, 576), "4:3 fit-width");
    }

    public static void TwentyOneByNine()
    {
        // fit-height: scale 1, width 2560 -> destination spans the canvas, 320 source columns cropped on each side.
        Expect(Compute(FitHeight.FitId, 2560, 1080), (0, 0, 1920, 1080), (320, 0, 1920, 1080), "21:9 fit-height");
        // fit-width: scale 0.75, height 810 -> bars 135 top and bottom, full source.
        Expect(Compute(FitWidth.FitId, 2560, 1080), (0, 135, 1920, 810), (0, 0, 2560, 1080), "21:9 fit-width");
    }

    public static void Portrait()
    {
        // 9:16 source. fit-height: scale 0.5625, width 607.5 centered. fit-width: scale 16/9, crop 607.5 source rows centered.
        Expect(Compute(FitHeight.FitId, 1080, 1920), (656.25, 0, 607.5, 1080), (0, 0, 1080, 1920), "portrait fit-height");
        Expect(Compute(FitWidth.FitId, 1080, 1920), (0, 0, 1920, 1080), (0, 656.25, 1080, 607.5), "portrait fit-width");
        // Portrait canvas with a landscape source: bars top/bottom under fit-width, crop under fit-height.
        Expect(Compute(FitWidth.FitId, 1920, 1080, 1080, 1920), (0, 656.25, 1080, 607.5), (0, 0, 1920, 1080), "landscape on portrait fit-width");
        Expect(Compute(FitHeight.FitId, 1920, 1080, 1080, 1920), (0, 0, 1080, 1920), (656.25, 0, 607.5, 1080), "landscape on portrait fit-height");
    }

    public static void OnePixel()
    {
        Expect(Compute(FitHeight.FitId, 1, 1), (420, 0, 1080, 1080), (0, 0, 1, 1), "1x1 fit-height");
        Expect(Compute(FitWidth.FitId, 1, 1), (0, 0, 1920, 1080), (0, 0.21875, 1, 0.5625), "1x1 fit-width"); // crop 1080/1920 of the pixel.
        Expect(Compute(FitHeight.FitId, 1, 1, 1, 1), (0, 0, 1, 1), (0, 0, 1, 1), "1x1 on 1x1");
    }

    public static void LargerThanCanvas()
    {
        // 3840x1600 (2.4:1). fit-height: scale 0.675, width 2592 -> crop 1920/0.675 = 2844.444... source columns, 497.777... each side.
        Expect(Compute(FitHeight.FitId, 3840, 1600), (0, 0, 1920, 1080), (497.77777777777777, 0, 2844.4444444444443, 1600), "2.4:1 fit-height");
        // fit-width: scale 0.5, height 800 -> bars 140 top and bottom.
        Expect(Compute(FitWidth.FitId, 3840, 1600), (0, 140, 1920, 800), (0, 0, 3840, 1600), "2.4:1 fit-width");
        // 8K 4:3 source: fit-height bars, fit-width crop, sizes far beyond the canvas.
        Expect(Compute(FitHeight.FitId, 8192, 6144), (240, 0, 1440, 1080), (0, 0, 8192, 6144), "8192x6144 fit-height");
        Expect(Compute(FitWidth.FitId, 8192, 6144), (0, 0, 1920, 1080), (0, 768, 8192, 4608), "8192x6144 fit-width");
    }

    public static void RegistryFallback()
    {
        var canvas = new CanvasSettings(1920, 1080, FitHeight.FitId);
        Check(Registry.Resolve(new ClipPlacement(null), canvas, out var warning).Id == FitHeight.FitId && warning == null, "Null id must inherit the default silently.");
        Check(Registry.Resolve(new ClipPlacement("stretch"), canvas, out warning).Id == FitHeight.FitId && warning != null && warning.Contains("stretch"), "Unknown id must fall back with a warning.");
        Check(Registry.Resolve(new ClipPlacement(FitWidth.FitId), canvas, out warning).Id == FitWidth.FitId && warning == null, "Registered id must resolve itself.");
        Check(Registry.Resolve(new ClipPlacement(null), new CanvasSettings(1920, 1080, FitWidth.FitId), out _).Id == FitWidth.FitId, "Default follows the canvas setting.");
        Throws<InvalidOperationException>(() => Registry.Resolve(new ClipPlacement(null), new CanvasSettings(1920, 1080, "missing"), out _));
        Expect(Registry.Compute(new ClipPlacement("unknown"), canvas, 2560, 1080, out string fitId, out warning), (0, 0, 1920, 1080), (320, 0, 1920, 1080), "compute via registry");
        Check(fitId == FitHeight.FitId && warning != null, "Registry compute must report the applied id and the fallback.");
        Check(CanvasSettings.Default == new CanvasSettings(1920, 1080, FitHeight.FitId), "New-project default differs from the spec.");
        Check(Registry.Ids.OrderBy(i => i, StringComparer.Ordinal).SequenceEqual([FitHeight.FitId, FitWidth.FitId]), "Built-in ids differ.");
        Throws<ArgumentException>(() => FitRegistry.CreateDefault().Register(new FitHeight()));
    }

    public static void InvalidSizes()
    {
        foreach (var calculator in new IFitCalculator[] { new FitHeight(), new FitWidth() })
            foreach (var (w, h, cw, ch) in new[] { (0, 1080, 1920, 1080), (1920, 0, 1920, 1080), (-1, 1080, 1920, 1080), (1920, 1080, 0, 1080), (1920, 1080, 1920, 0), (1920, 1080, 1920, -5) })
                Throws<ArgumentOutOfRangeException>(() => calculator.Compute(w, h, cw, ch));
        Throws<ArgumentOutOfRangeException>(() => new CanvasSettings(0, 1080, FitHeight.FitId));
        Throws<ArgumentOutOfRangeException>(() => new CanvasSettings(1920, -1, FitHeight.FitId));
        Throws<ArgumentException>(() => new CanvasSettings(1920, 1080, ""));
    }

    public static void SourceSizeOption()
    {
        var o = Options.Parse(["--source", "contract-fake", "--source-size", "2560x1080"]);
        Check(o.SourceWidth == 2560 && o.SourceHeight == 1080, "Source size not parsed.");
        o = Options.Parse(["--width", "1280", "--height", "720"]);
        Check(o.SourceWidth == 1280 && o.SourceHeight == 720, "Default source size must equal the canvas.");
        Check(Options.Parse(["--source-size", "1920x1080"]).SourceWidth == 1920, "Canvas-sized source with the pattern source must be accepted.");
        Throws<ArgumentException>(() => Options.Parse(["--source-size", "2560x1080"])); // pattern source: no placement.
        Throws<ArgumentException>(() => Options.Parse(["--source", "contract-fake", "--source-size", "2560"]));
        Throws<ArgumentException>(() => Options.Parse(["--source", "contract-fake", "--source-size", "0x1080"]));
        Throws<ArgumentException>(() => Options.Parse(["--source", "contract-fake", "--source-size", "8193x1080"]));
        Throws<ArgumentException>(() => Options.Parse(["--source", "contract-fake", "--source-size", "axb"]));
    }
}
