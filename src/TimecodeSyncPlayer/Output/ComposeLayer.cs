using TimecodeSyncPlayer.Contracts;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

internal enum LayerAction { DrawAcquired, DrawHeld, DrawFrozen, DrawBlack }

/// <summary>
/// 合成レイヤーの純粋な選択規則。準備待ち・読込失敗で黒を挿入せず、
/// 最後に合成したキャンバス（Held）を保持する。Freeze は保存済みのソース画像を使う。
/// </summary>
internal static class ComposeLayerPolicy
{
    public static LayerAction Decide(OutputGapMode gap, bool hasAcquired, bool hasHeld, bool hasFrozen) => gap switch
    {
        OutputGapMode.Black => LayerAction.DrawBlack,
        OutputGapMode.Hold => hasHeld ? LayerAction.DrawHeld : LayerAction.DrawBlack,
        OutputGapMode.GapFreeze => hasFrozen ? LayerAction.DrawFrozen : hasHeld ? LayerAction.DrawHeld : LayerAction.DrawBlack,
        _ => hasAcquired ? LayerAction.DrawAcquired : hasHeld ? LayerAction.DrawHeld : LayerAction.DrawBlack
    };

    /// <summary>
    /// 描画に使う配置。Freeze は確定時点の配置、それ以外は現在クリップの配置。
    /// Held は合成済みキャンバスの複製をそのまま重ねるため配置を選ばない（D26）。
    /// </summary>
    public static ClipPlacement SelectPlacement(LayerAction action, ClipPlacement current, ClipPlacement frozen) => action switch
    {
        LayerAction.DrawFrozen => frozen,
        _ => current
    };
}

/// <summary>
/// 合成に使う 1 枚の画像。View は描画用 SRV、RawTexture は Freeze コピー用の生ポインタ。
/// Lease と Owner は layer が保持する場合に所有し、置換時に返却する。
/// （mpv 経路は ring slot の Surface を借用するため Owner は null。GStreamer 経路は
/// AddRef した SRV/テクスチャを Surface として Owner に持つ）
/// </summary>
internal readonly record struct LayerImage(
    ID3D11ShaderResourceView View,
    IntPtr RawTexture,
    int Width,
    int Height,
    ISourceImageLease? Lease,
    IDisposable? Owner)
{
    public void Release()
    {
        Owner?.Dispose();
        Lease?.Dispose();
    }
}

/// <summary>
/// 固定キャンバスへの合成。配置（CanvasPlacement）、Held（直前に合成したキャンバスの所有コピー）、
/// Freeze（目標位置のソース画像の GPU コピー）、テストカードを扱う。
/// カードは通常映像の確定画像とは別管理とし、カード合成後の画像を Freeze に使わない。
/// 単一の GPU worker が所有する。
/// </summary>
internal sealed class ComposeLayer : IDisposable
{
    // Freeze 保存を許す目標位置との差（秒）。D21-b のフレーム到着判定（±2 フレーム）と
    // 同じ意図で、25〜60fps の 1〜3 フレームに収まる値にする。
    private const double FreezeTargetToleranceSeconds = 0.05;

    private readonly GpuDevice gpu;
    private readonly ShaderPipeline shaders;
    private CanvasSettings canvas;
    private readonly FitRegistry fits = FitRegistry.CreateDefault();

    private Surface? frozen;
    private ClipPlacement frozenClip = new(null);
    private int frozenWidth, frozenHeight;

    // D26: Held は「直前に合成したキャンバスそのもの（黒を含む）」の所有コピー。
    // ソース（共有リングの面など）を参照しないため、世代切替で破棄する必要がない。
    private Surface? heldCanvas;
    private int heldCanvasWidth, heldCanvasHeight;

    // D26-b: Freeze 確定は「進入中（Hold）に取得した目標位置のソース画像」で行う。
    // 確定 tick（GapFreeze）では同じリースが続いて新しい画像が渡らないため、
    // 取得済みのソース画像を位置付きで追跡する。世代切替では破棄する（リング面の再利用）。
    private LayerImage? sourceFrame;
    private ClipPlacement sourceFrameClip = new(null);
    private double sourceFramePosition;
    private bool sourceFramePositionKnown;

    public ComposeLayer(GpuDevice gpu, ShaderPipeline shaders, CanvasSettings canvas)
    {
        this.gpu = gpu;
        this.shaders = shaders;
        this.canvas = canvas;
    }

    public bool HasHeld => heldCanvas != null;
    public bool HasFreeze => frozen != null;

    /// <summary>
    /// キャンバス寸法を差し替える（段階 4.2 SetCanvas）。Held は保持し、Freeze 用テクスチャは
    /// 破棄して次回必要時に作り直す（黒を挟まない）。placement は描画時に新しい寸法で計算される。
    /// </summary>
    public void SetCanvas(CanvasSettings value)
    {
        canvas = value;
        ClearFreeze();
    }

    /// <summary>Freeze 画像だけを破棄する（世代切替時）。Held は保持を続ける。</summary>
    public void ClearFreeze()
    {
        frozen?.Dispose();
        frozen = null;
        frozenClip = new(null);
        frozenWidth = frozenHeight = 0;
    }

    // D26-b: Freeze 候補として追跡している取得済みソース画像。世代切替でリングの面が
    // 再利用され得るため、破棄する（Held のキャンバスは所有コピーなので破棄しない）。
    public void ClearSourceFrame()
    {
        sourceFrame = null;
        sourceFrameClip = new(null);
        sourceFramePositionKnown = false;
    }

    /// <summary>
    /// 合成 1 tick 分。GapFreeze の確定は、確定 tick の新規取得フレーム、無ければ Freeze 進入中に
    /// 取得した目標位置のソース画像を使う。合成後はキャンバスを所有テクスチャへ複製して次 tick の
    /// Held にする（D26）。戻り値は渡された acquired を Held として保持したか（常に false）。
    /// </summary>
    public bool Compose(Surface target, OutputGapMode gap, ClipPlacement clip, bool testCardEnabled, ImageStamp cardStamp, long origin,
        LayerImage? acquired, double? acquirePositionSeconds = null, double? freezeTargetSeconds = null)
    {
        // D26/D26-b: Freeze として保存するのは「目標位置（現在の再生位置＝目標最終位置）に一致した
        // ソース画像」だけ。確定 tick（GapFreeze）では同じリースが続いて新しい画像が渡らないため、
        // 進入中に取得した画像を追跡しておき、それを使う。所有コピー（直前キャンバス）やジャンプ前の
        // フレームは凍結しない。目標フレームが届くまで frozen は null のまま、表示は Policy が Held を選ぶ。
        if (acquired != null)
            TrackSourceFrame(acquired.Value, clip, acquirePositionSeconds, freezeTargetSeconds);
        if (gap == OutputGapMode.GapFreeze && frozen == null)
        {
            if (acquired != null && acquirePositionSeconds.HasValue &&
                MatchesFreezeTarget(acquirePositionSeconds.Value, freezeTargetSeconds))
            {
                SaveFreeze(acquired.Value, clip);
            }
            else if (sourceFrame is { } tracked && sourceFramePositionKnown &&
                     MatchesFreezeTarget(sourceFramePosition, freezeTargetSeconds))
            {
                SaveFreeze(tracked, sourceFrameClip);
            }
        }

        LayerAction action = ComposeLayerPolicy.Decide(gap, acquired != null, HasHeld, frozen != null);
        ClipPlacement placement = ComposeLayerPolicy.SelectPlacement(action, clip, frozenClip);
        switch (action)
        {
            case LayerAction.DrawAcquired:
                Draw(target, acquired!.Value, placement);
                break;
            case LayerAction.DrawHeld:
                DrawHeldCanvas(target);
                break;
            case LayerAction.DrawFrozen:
                Draw(target, FrozenImage(), placement);
                break;
            default:
                shaders.Clear(target.Target!);
                break;
        }

        if (testCardEnabled)
            shaders.Compose(target, canvas.Width, canvas.Height, cardStamp, origin);

        // Held = 直前に合成したキャンバスそのもの（黒を含む）。DrawHeld の tick は
        // 既に同じ内容なのでコピーしない（それ以外は取得・Freeze・黒のいずれも更新する）。
        if (action != LayerAction.DrawHeld)
            RememberCanvas(target);

        return false; // ソース画像は保持しない（所有コピーへ複製して返す）
    }

    private LayerImage FrozenImage()
    {
        // Freeze はここが所有する専用テクスチャ。SRV は frozen と同時に破棄される。
        return new LayerImage(frozen!.View, frozen.Texture.NativePointer, frozenWidth, frozenHeight, null, null);
    }

    /// <summary>合成後のキャンバスを所有テクスチャへ複製する（寸法変更時は作り直す）。</summary>
    private void RememberCanvas(Surface target)
    {
        if (heldCanvas == null || heldCanvasWidth != canvas.Width || heldCanvasHeight != canvas.Height)
        {
            heldCanvas?.Dispose();
            heldCanvas = new Surface(gpu, gpu.Texture(canvas.Width, canvas.Height, SourceSharing.None), false, SourceSharing.None);
            heldCanvasWidth = canvas.Width;
            heldCanvasHeight = canvas.Height;
        }
        NativeTextureOps.CopyResource(gpu.Context, heldCanvas.Texture.NativePointer, target.Texture.NativePointer);
    }

    /// <summary>直前キャンバスを重ねる。寸法が一致すればコピー、違えば（キャンバス変更）配置して描く。</summary>
    private void DrawHeldCanvas(Surface target)
    {
        if (heldCanvasWidth == canvas.Width && heldCanvasHeight == canvas.Height)
        {
            NativeTextureOps.CopyResource(gpu.Context, target.Texture.NativePointer, heldCanvas!.Texture.NativePointer);
            return;
        }

        var placement = fits.Compute(new ClipPlacement(null), canvas, heldCanvasWidth, heldCanvasHeight, out _, out _);
        shaders.PlaceView(heldCanvas!.View, heldCanvasWidth, heldCanvasHeight, target.Target!, canvas.Width, canvas.Height, placement);
    }

    /// <summary>取得画像の位置が Freeze 目標（目標最終フレームの位置）に一致するか。</summary>
    private static bool MatchesFreezeTarget(double positionSeconds, double? freezeTargetSeconds) =>
        freezeTargetSeconds.HasValue &&
        Math.Abs(positionSeconds - freezeTargetSeconds.Value) <= FreezeTargetToleranceSeconds;

    /// <summary>
    /// Freeze 候補のソース画像を位置付きで更新する。目標位置に一致している追跡画像を、
    /// 目標外のフレーム（遅れて届いた別位置）で上書きしない。
    /// </summary>
    private void TrackSourceFrame(LayerImage image, ClipPlacement clip, double? positionSeconds, double? freezeTargetSeconds)
    {
        bool acquiredMatches = positionSeconds.HasValue && MatchesFreezeTarget(positionSeconds.Value, freezeTargetSeconds);
        bool trackedMatches = sourceFrame.HasValue && sourceFramePositionKnown &&
                              MatchesFreezeTarget(sourceFramePosition, freezeTargetSeconds);
        if (!acquiredMatches && trackedMatches) return;
        sourceFrame = image;
        sourceFrameClip = clip;
        sourceFramePosition = positionSeconds ?? double.NaN;
        sourceFramePositionKnown = positionSeconds.HasValue;
    }

    private void SaveFreeze(LayerImage image, ClipPlacement clip)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        if (frozen == null || frozenWidth != image.Width || frozenHeight != image.Height)
        {
            frozen?.Dispose();
            frozen = new Surface(gpu, gpu.Texture(image.Width, image.Height, SourceSharing.None), false, SourceSharing.None);
            frozenWidth = image.Width;
            frozenHeight = image.Height;
        }
        NativeTextureOps.CopyResource(gpu.Context, frozen.Texture.NativePointer, image.RawTexture);
        frozenClip = clip;
    }

    private void Draw(Surface target, LayerImage image, ClipPlacement clip)
    {
        if (image.Width <= 0 || image.Height <= 0) { shaders.Clear(target.Target!); return; }
        var placement = fits.Compute(clip, canvas, image.Width, image.Height, out _, out _);
        shaders.PlaceView(image.View, image.Width, image.Height, target.Target!, canvas.Width, canvas.Height, placement);
    }

    public void Dispose()
    {
        ClearFreeze();
        heldCanvas?.Dispose();
        heldCanvas = null;
    }
}
