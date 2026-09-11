using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Output;

internal enum LayerAction { DrawAcquired, DrawHeld, DrawFrozen, DrawBlack }

/// <summary>
/// 合成レイヤーの純粋な選択規則。準備待ち・読込失敗で黒を挿入せず、
/// 最後に確定した画像（Held）を保持する。Freeze は保存済みのソース画像を使う。
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
}

/// <summary>
/// 固定キャンバスへの合成。配置（CanvasPlacement）、Held（最後に確定したソース画像）、
/// Freeze（合成前のソース画像の GPU コピー）、テストカードを扱う。
/// カードは通常映像の確定画像とは別管理とし、カード合成後の画像を Freeze に使わない。
/// 単一の GPU worker が所有する。
/// </summary>
internal sealed class ComposeLayer : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly ShaderPipeline shaders;
    private readonly CanvasSettings canvas;
    private readonly FitRegistry fits = FitRegistry.CreateDefault();

    // Held: ソース側 slot への借用参照と、その slot を再利用させない lease。
    private Surface? held;
    private ISourceImageLease? heldLease;
    private int heldWidth, heldHeight;

    // Freeze: ここが所有する専用テクスチャ。
    private Surface? frozen;
    private int frozenWidth, frozenHeight;

    public ComposeLayer(GpuDevice gpu, ShaderPipeline shaders, CanvasSettings canvas)
    {
        this.gpu = gpu;
        this.shaders = shaders;
        this.canvas = canvas;
    }

    public bool HasHeld => held != null;
    public bool HasFreeze => frozen != null;

    /// <summary>Freeze 画像だけを破棄する（世代切替時）。Held は保持を続ける。</summary>
    public void ClearFreeze()
    {
        frozen?.Dispose();
        frozen = null;
        frozenWidth = frozenHeight = 0;
    }

    /// <summary>
    /// 合成 1 tick 分。GapFreeze への進入時は、合成前のソース画像を GPU コピーで保存してから描く。
    /// 戻り値は渡された acquiredLease を Held として保持したか（true のとき呼び出し側は返却しない）。
    /// </summary>
    public bool Compose(Surface target, OutputGapMode gap, ClipPlacement clip, bool testCardEnabled, ImageStamp cardStamp, long origin,
        ISourceImageLease? acquiredLease, Surface? acquired, int acquiredWidth, int acquiredHeight)
    {
        if (gap == OutputGapMode.GapFreeze && frozen == null)
        {
            if (acquired != null) KeepHeld(acquiredLease, acquired, acquiredWidth, acquiredHeight);
            SaveFreeze();
        }
        else if (gap != OutputGapMode.Black && acquired != null)
        {
            KeepHeld(acquiredLease, acquired, acquiredWidth, acquiredHeight);
        }

        switch (ComposeLayerPolicy.Decide(gap, acquired != null, held != null, frozen != null))
        {
            case LayerAction.DrawAcquired:
                Draw(target, acquired!, acquiredWidth, acquiredHeight, clip);
                break;
            case LayerAction.DrawHeld:
                Draw(target, held!, heldWidth, heldHeight, clip);
                break;
            case LayerAction.DrawFrozen:
                Draw(target, frozen!, frozenWidth, frozenHeight, clip);
                break;
            default:
                shaders.Clear(target.Target!);
                break;
        }

        if (testCardEnabled)
            shaders.Compose(target, canvas.Width, canvas.Height, cardStamp, origin);

        return acquiredLease != null && ReferenceEquals(heldLease, acquiredLease);
    }

    private void KeepHeld(ISourceImageLease? lease, Surface source, int width, int height)
    {
        if (lease != null && !ReferenceEquals(heldLease, lease))
        {
            var previous = heldLease;
            heldLease = lease;
            previous?.Dispose();
        }
        held = source;
        heldWidth = width;
        heldHeight = height;
    }

    private void SaveFreeze()
    {
        if (held == null) return;
        if (frozen == null || frozenWidth != heldWidth || frozenHeight != heldHeight)
        {
            frozen?.Dispose();
            frozen = new Surface(gpu, gpu.Texture(heldWidth, heldHeight, SourceSharing.None), false, SourceSharing.None);
            frozenWidth = heldWidth;
            frozenHeight = heldHeight;
        }
        gpu.Context.CopyResource(frozen.Texture, held.Texture);
    }

    private void Draw(Surface target, Surface source, int sourceWidth, int sourceHeight, ClipPlacement clip)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) { shaders.Clear(target.Target!); return; }
        var placement = fits.Compute(clip, canvas, sourceWidth, sourceHeight, out _, out _);
        shaders.Place(source, sourceWidth, sourceHeight, target.Target!, canvas.Width, canvas.Height, placement);
    }

    public void Dispose()
    {
        ClearFreeze();
        var lease = heldLease;
        heldLease = null; held = null;
        lease?.Dispose();
    }
}
