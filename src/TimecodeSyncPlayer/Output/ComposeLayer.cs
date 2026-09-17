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
/// 固定キャンバスへの合成。配置（CanvasPlacement）、Held（最後に確定したソース画像）、
/// Freeze（合成前のソース画像の GPU コピー）、テストカードを扱う。
/// カードは通常映像の確定画像とは別管理とし、カード合成後の画像を Freeze に使わない。
/// 単一の GPU worker が所有する。
/// </summary>
internal sealed class ComposeLayer : IDisposable
{
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

    /// <summary>
    /// 合成 1 tick 分。GapFreeze への進入時は、合成前のソース画像を GPU コピーで保存してから描く。
    /// 合成後はキャンバスを所有テクスチャへ複製して次 tick の Held にする（D26）。
    /// 戻り値は渡された acquired を Held として保持したか（常に false。所有コピーを使う）。
    /// </summary>
    public bool Compose(Surface target, OutputGapMode gap, ClipPlacement clip, bool testCardEnabled, ImageStamp cardStamp, long origin,
        LayerImage? acquired)
    {
        if (gap == OutputGapMode.GapFreeze && frozen == null)
        {
            if (acquired != null) SaveFreeze(acquired.Value, clip);
            else if (heldCanvas != null) SaveFreeze(LastCanvasImage(), new ClipPlacement(null));
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

    private LayerImage LastCanvasImage() =>
        new(heldCanvas!.View, heldCanvas.Texture.NativePointer, heldCanvasWidth, heldCanvasHeight, null, null);

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
