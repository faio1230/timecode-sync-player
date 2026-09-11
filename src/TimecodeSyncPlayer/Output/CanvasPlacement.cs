namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 固定キャンバスの配置計算（docs/CANVAS-PLACEMENT-SPEC.md）。純粋計算で D3D に依存しない。回転は事前反映済みとする。
/// 規約: Destination は常にキャンバス内。はみ出しは Destination を外へ出さず SourceCrop（素材座標）を狭めて表す。
/// 値は実数で持ち、丸めは描画側が行う。試作 scripts/GpuOutputProbe の CanvasPlacement を移植。
/// </summary>
internal sealed record CanvasSettings(int Width, int Height, string DefaultFitId)
{
    public static readonly CanvasSettings Default = new(1920, 1080, FitHeight.FitId);
    public int Width { get; } = Width > 0 ? Width : throw new ArgumentOutOfRangeException(nameof(Width), "Canvas width must be positive.");
    public int Height { get; } = Height > 0 ? Height : throw new ArgumentOutOfRangeException(nameof(Height), "Canvas height must be positive.");
    public string DefaultFitId { get; } = !string.IsNullOrEmpty(DefaultFitId) ? DefaultFitId : throw new ArgumentException("Default fit id must not be empty.", nameof(DefaultFitId));
}

internal sealed record ClipPlacement(string? FitId); // null はプロジェクト既定を継承。

internal readonly record struct PlacementRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

internal readonly record struct Placement(PlacementRect Destination, PlacementRect SourceCrop);

internal interface IFitCalculator
{
    string Id { get; }
    Placement Compute(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight);
}

/// <summary>
/// fit 方式が共有する等比・中央揃え配置。合わせた辺はキャンバス寸法そのものにし、
/// canvas/src*src の丸め残差がサブピクセルの切り落とし・余白にならないようにする。
/// </summary>
internal static class UniformPlacement
{
    public static Placement Centered(double scale, int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight, bool widthFitted, bool heightFitted)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source size must be positive.");
        if (canvasWidth <= 0 || canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth), "Canvas size must be positive.");
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var (dx, dw, cx, cw) = Axis(scale, sourceWidth, canvasWidth, widthFitted);
        var (dy, dh, cy, ch) = Axis(scale, sourceHeight, canvasHeight, heightFitted);
        return new(new(dx, dy, dw, dh), new(cx, cy, cw, ch));
    }

    private static (double Offset, double Size, double CropOffset, double CropSize) Axis(double scale, int source, int canvas, bool fitted)
    {
        double scaled = fitted ? canvas : source * scale;
        if (scaled <= canvas) return ((canvas - scaled) / 2, scaled, 0, source);
        double crop = canvas / scale;
        return (0, canvas, (source - crop) / 2, crop);
    }
}

internal sealed class FitHeight : IFitCalculator
{
    public const string FitId = "fit-height";
    public string Id => FitId;
    public Placement Compute(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
    {
        if (sourceHeight <= 0 || canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight), "Heights must be positive.");
        return UniformPlacement.Centered(canvasHeight / (double)sourceHeight, sourceWidth, sourceHeight, canvasWidth, canvasHeight, false, true);
    }
}

internal sealed class FitWidth : IFitCalculator
{
    public const string FitId = "fit-width";
    public string Id => FitId;
    public Placement Compute(int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
    {
        if (sourceWidth <= 0 || canvasWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Widths must be positive.");
        return UniformPlacement.Centered(canvasWidth / (double)sourceWidth, sourceWidth, sourceHeight, canvasWidth, canvasHeight, true, false);
    }
}

/// <summary>
/// fit 方式のレジストリ。Resolve: clip の null は既定を無警告で継承、未知 ID は既定へフォールバックして warning。
/// 既定 ID 自体が未登録なら構成エラーとして例外。
/// </summary>
internal sealed class FitRegistry
{
    private readonly Dictionary<string, IFitCalculator> calculators = new(StringComparer.Ordinal);
    public static FitRegistry CreateDefault() => new FitRegistry().Register(new FitHeight()).Register(new FitWidth());
    public IReadOnlyCollection<string> Ids => calculators.Keys;

    public FitRegistry Register(IFitCalculator calculator)
    {
        if (string.IsNullOrEmpty(calculator.Id)) throw new ArgumentException("Fit id must not be empty.", nameof(calculator));
        if (!calculators.TryAdd(calculator.Id, calculator)) throw new ArgumentException($"Fit id '{calculator.Id}' is already registered.", nameof(calculator));
        return this;
    }

    public bool TryGet(string id, out IFitCalculator calculator) => calculators.TryGetValue(id, out calculator!);

    public IFitCalculator Resolve(ClipPlacement clip, CanvasSettings canvas, out string? warning)
    {
        if (!TryGet(canvas.DefaultFitId, out var fallback)) throw new InvalidOperationException($"Project default fit id '{canvas.DefaultFitId}' is not registered.");
        warning = null;
        if (clip.FitId == null) return fallback;
        if (TryGet(clip.FitId, out var calculator)) return calculator;
        warning = $"Unknown fit id '{clip.FitId}'; using project default '{canvas.DefaultFitId}'.";
        return fallback;
    }

    public Placement Compute(ClipPlacement clip, CanvasSettings canvas, int sourceWidth, int sourceHeight, out string fitId, out string? warning)
    {
        var calculator = Resolve(clip, canvas, out warning);
        fitId = calculator.Id;
        return calculator.Compute(sourceWidth, sourceHeight, canvas.Width, canvas.Height);
    }
}
