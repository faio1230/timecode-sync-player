using TimecodeSyncPlayer.Contracts;
using Vortice.Direct3D11;

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

    private LayerImage? held;
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

    /// <summary>Held を破棄する（GStreamer の世代切替時）。</summary>
    public void ClearHeld()
    {
        held?.Release();
        held = null;
    }

    /// <summary>
    /// 合成 1 tick 分。GapFreeze への進入時は、合成前のソース画像を GPU コピーで保存してから描く。
    /// 戻り値は渡された acquired を Held として保持したか（true のとき呼び出し側は返却しない）。
    /// </summary>
    public bool Compose(Surface target, OutputGapMode gap, ClipPlacement clip, bool testCardEnabled, ImageStamp cardStamp, long origin,
        LayerImage? acquired)
    {
        if (gap == OutputGapMode.GapFreeze && frozen == null)
        {
            if (acquired != null) KeepHeld(acquired.Value);
            SaveFreeze();
        }
        else if (gap != OutputGapMode.Black && acquired != null)
        {
            KeepHeld(acquired.Value);
        }

        switch (ComposeLayerPolicy.Decide(gap, acquired != null, held != null, frozen != null))
        {
            case LayerAction.DrawAcquired:
                Draw(target, acquired!.Value, clip);
                break;
            case LayerAction.DrawHeld:
                Draw(target, held!.Value, clip);
                break;
            case LayerAction.DrawFrozen:
                Draw(target, FrozenImage(), clip);
                break;
            default:
                shaders.Clear(target.Target!);
                break;
        }

        if (testCardEnabled)
            shaders.Compose(target, canvas.Width, canvas.Height, cardStamp, origin);

        return acquired != null && held.HasValue && ReferenceEquals(held.Value.Lease, acquired.Value.Lease);
    }

    private LayerImage FrozenImage()
    {
        // Freeze はここが所有する専用テクスチャ。SRV は frozen と同時に破棄される。
        return new LayerImage(frozen!.View, frozen.Texture.NativePointer, frozenWidth, frozenHeight, null, null);
    }

    private void KeepHeld(LayerImage source)
    {
        if (held is { } previous && (previous.Lease != null || previous.Owner != null))
        {
            if (ReferenceEquals(previous.Lease, source.Lease) && ReferenceEquals(previous.Owner, source.Owner)) return;
            previous.Release();
        }
        held = source;
    }

    private void SaveFreeze()
    {
        if (held is not { } image) return;
        if (frozen == null || frozenWidth != image.Width || frozenHeight != image.Height)
        {
            frozen?.Dispose();
            frozen = new Surface(gpu, gpu.Texture(image.Width, image.Height, SourceSharing.None), false, SourceSharing.None);
            frozenWidth = image.Width;
            frozenHeight = image.Height;
        }
        NativeTextureOps.CopyResource(gpu.Context, frozen.Texture.NativePointer, image.RawTexture);
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
        held?.Release();
        held = null;
    }
}
