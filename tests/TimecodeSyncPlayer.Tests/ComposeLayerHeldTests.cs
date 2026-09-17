using System.Runtime.InteropServices;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D26: 合成レイヤーの Held は合成側が所有する「直前キャンバスの複製」で、世代切替で
/// ソースの面を参照しない（ClearFreeze では破棄されない）。NotReady では黒を挟まず、
/// 黒は Black gap と起動直後（1 枚も描いていない）だけ。
/// GPU が必要なため、D3D11 デバイスを作れない環境ではスキップする。
/// </summary>
public sealed class ComposeLayerHeldTests
{
    [SkippableFact]
    public void Startup_WithNoFrames_DrawsBlack()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);

        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, null);

        Color4 pixel = env.ReadCenterPixel(target);
        pixel.R.Should().BeLessThan(0.02f);
        pixel.G.Should().BeLessThan(0.02f);
        pixel.B.Should().BeLessThan(0.02f);
    }

    [SkippableFact]
    public void NotReadyAfterFirstFrame_DrawsHeldCanvas()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var green = new SolidSource(env, 64, 64, new Color4(0f, 1f, 0f, 1f));

        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, green.Image);

        // 世代切替で共有リングの面が再利用される状況を模擬する（取得元を赤で上書き）。
        // Held がソースの面を参照していると赤が出る。所有コピーなら緑のまま。
        env.Fill(green.Surface, new Color4(1f, 0f, 0f, 1f));

        bool retained = layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, null);

        retained.Should().BeFalse("ソース画像は保持せず所有コピーを使う");
        Color4 pixel = env.ReadCenterPixel(target);
        pixel.G.Should().BeGreaterThan(0.9f, "直前のキャンバス（緑）を描く");
        pixel.R.Should().BeLessThan(0.1f);
        pixel.B.Should().BeLessThan(0.1f);
    }

    [SkippableFact]
    public void GenerationChange_ClearFreeze_KeepsHeldCanvas()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var blue = new SolidSource(env, 64, 64, new Color4(0f, 0f, 1f, 1f));

        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, blue.Image);
        layer.ClearFreeze(); // 世代切替で残る処理（Freeze だけ破棄）
        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, null);

        Color4 pixel = env.ReadCenterPixel(target);
        pixel.B.Should().BeGreaterThan(0.9f, "世代切替後も直前のキャンバス（青）を保持する");
        pixel.R.Should().BeLessThan(0.1f);
        pixel.G.Should().BeLessThan(0.1f);
    }

    [SkippableFact]
    public void BlackGap_DrawsBlack_AndKeepsBlackAsHeldCanvas()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var green = new SolidSource(env, 64, 64, new Color4(0f, 1f, 0f, 1f));

        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, green.Image);
        layer.Compose(target.Surface, OutputGapMode.Black, new ClipPlacement(null), false, default, 0, null);

        Color4 black = env.ReadCenterPixel(target);
        black.R.Should().BeLessThan(0.02f);
        black.G.Should().BeLessThan(0.02f);
        black.B.Should().BeLessThan(0.02f);

        // Held = 直前に合成したキャンバスそのもの（黒を含む）。Black ギャップ明けで
        // 新フレームが届くまでは黒のまま（ギャップ前の映像を一瞬戻さない）。
        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0, null);
        Color4 held = env.ReadCenterPixel(target);
        held.R.Should().BeLessThan(0.02f, "Black ギャップの黒が最後のキャンバスとして続く");
        held.G.Should().BeLessThan(0.02f);
        held.B.Should().BeLessThan(0.02f);
    }

    [SkippableFact]
    public void GapFreeze_SavesOnlyTheFrameAtTheTargetPosition()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var red = new SolidSource(env, 64, 64, new Color4(1f, 0f, 0f, 1f));
        using var yellow = new SolidSource(env, 64, 64, new Color4(1f, 1f, 0f, 1f));

        // 目標と違う位置のフレームは凍結しない（ジャンプ前の絵を保存しない）。
        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            red.Image, acquirePositionSeconds: 10.0, freezeTargetSeconds: 19.967);
        layer.HasFreeze.Should().BeFalse("目標位置でないフレームは Freeze として確定しない");

        // 目標位置のフレームで確定する。
        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            yellow.Image, acquirePositionSeconds: 19.967, freezeTargetSeconds: 19.967);
        layer.HasFreeze.Should().BeTrue();
        Color4 pixel = env.ReadCenterPixel(target);
        pixel.G.Should().BeGreaterThan(0.9f, "目標位置のフレーム（黄）を表示する");
        pixel.R.Should().BeGreaterThan(0.9f);
        pixel.B.Should().BeLessThan(0.1f);

        // 確定後は凍結したフレームのまま（目標外のフレームでは更新しない）。
        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            red.Image, acquirePositionSeconds: 10.0, freezeTargetSeconds: 19.967);
        pixel = env.ReadCenterPixel(target);
        pixel.G.Should().BeGreaterThan(0.9f);
        pixel.B.Should().BeLessThan(0.1f);
    }

    [SkippableFact]
    public void GapFreeze_UsesTargetFrameAcquiredWhileEntering_WhenCompletionTickHasNoNewFrame()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var red = new SolidSource(env, 64, 64, new Color4(1f, 0f, 0f, 1f));
        using var yellow = new SolidSource(env, 64, 64, new Color4(1f, 1f, 0f, 1f));

        // 再生中の赤を Held（直前キャンバス）にした後、Freeze 進入中（Hold）に目標位置の黄が届く。
        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0,
            red.Image, acquirePositionSeconds: 10.0, freezeTargetSeconds: 10.0);
        layer.Compose(target.Surface, OutputGapMode.Hold, new ClipPlacement(null), false, default, 0,
            yellow.Image, acquirePositionSeconds: 19.967, freezeTargetSeconds: 19.967);

        // 確定（FreezeComplete）の tick では同じリースが続くため新しいフレームは渡らない。
        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            null, acquirePositionSeconds: null, freezeTargetSeconds: 19.967);

        layer.HasFreeze.Should().BeTrue("進入中に取得した目標位置のフレームで確定する");
        Color4 pixel = env.ReadCenterPixel(target);
        pixel.G.Should().BeGreaterThan(0.9f, "直前キャンバス（赤）ではなく目標フレーム（黄）を表示する");
        pixel.R.Should().BeGreaterThan(0.9f);
        pixel.B.Should().BeLessThan(0.1f);
    }

    [SkippableFact]
    public void GapFreeze_DoesNotFreezeStaleSourceFrame_WhenTargetFrameNeverArrives()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var red = new SolidSource(env, 64, 64, new Color4(1f, 0f, 0f, 1f));

        // ジャンプ前のフレーム（位置 10.0）だけが取得済み。目標フレームが届かないまま確定しても凍結しない。
        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0,
            red.Image, acquirePositionSeconds: 10.0, freezeTargetSeconds: 10.0);
        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            null, acquirePositionSeconds: null, freezeTargetSeconds: 19.967);

        layer.HasFreeze.Should().BeFalse("ジャンプ前の位置のフレームを Freeze として確定しない");
    }

    [SkippableFact]
    public void ClearSourceFrame_DropsTrackedFrame_AtGenerationChange()
    {
        using GpuEnvironment? env = GpuEnvironment.TryCreate();
        Skip.If(env is null, "D3D11 デバイスを作成できない環境");
        using var layer = env!.CreateLayer(64, 64);
        using TargetSurface target = env.CreateTarget(64, 64);
        using var yellow = new SolidSource(env, 64, 64, new Color4(1f, 1f, 0f, 1f));

        layer.Compose(target.Surface, OutputGapMode.None, new ClipPlacement(null), false, default, 0,
            yellow.Image, acquirePositionSeconds: 19.967, freezeTargetSeconds: 19.967);

        // 世代切替（load / seek）でリングの面が再利用され得るため、追跡中のソースを捨てる。
        layer.ClearSourceFrame();

        layer.Compose(target.Surface, OutputGapMode.GapFreeze, new ClipPlacement(null), false, default, 0,
            null, acquirePositionSeconds: null, freezeTargetSeconds: 19.967);

        layer.HasFreeze.Should().BeFalse("世代をまたいでソースの面を Freeze に使わない");
    }

    private sealed class SolidSource : IDisposable
    {
        public Surface Surface { get; }
        public LayerImage Image { get; }

        public SolidSource(GpuEnvironment env, int width, int height, Color4 color)
        {
            Surface = env.CreateSolid(width, height, color);
            Image = new LayerImage(Surface.View, Surface.Texture.NativePointer, width, height, null, null);
        }

        public void Dispose() => Surface.Dispose();
    }

    private sealed class TargetSurface : IDisposable
    {
        public Surface Surface { get; }
        public int Width { get; }
        public int Height { get; }

        public TargetSurface(GpuEnvironment env, int width, int height)
        {
            Width = width;
            Height = height;
            Surface = new Surface(env.Gpu, env.Gpu.Texture(width, height, SourceSharing.None), true, SourceSharing.None);
        }

        public void Dispose() => Surface.Dispose();
    }

    private sealed class GpuEnvironment : IDisposable
    {
        private ID3D11Texture2D? staging;
        private int stagingWidth, stagingHeight;

        public GpuDevice Gpu { get; }
        public ShaderPipeline Shaders { get; }

        private GpuEnvironment(GpuDevice gpu, ShaderPipeline shaders)
        {
            Gpu = gpu;
            Shaders = shaders;
        }

        public static GpuEnvironment? TryCreate()
        {
            GpuDevice? gpu = null;
            try
            {
                gpu = new GpuDevice(luid: null, fault: _ => { });
                return new GpuEnvironment(gpu, new ShaderPipeline(gpu));
            }
            catch (Exception)
            {
                gpu?.Dispose();
                return null;
            }
        }

        public ComposeLayer CreateLayer(int width, int height) =>
            new(Gpu, Shaders, new CanvasSettings(width, height, FitHeight.FitId));

        public TargetSurface CreateTarget(int width, int height) => new(this, width, height);

        public Surface CreateSolid(int width, int height, Color4 color)
        {
            var surface = new Surface(Gpu, Gpu.Texture(width, height, SourceSharing.None), true, SourceSharing.None);
            Gpu.Context.ClearRenderTargetView(surface.Target!, color);
            Gpu.Fence.Wait("test.solid");
            return surface;
        }

        /// <summary>取得元の面を別の色で上書きする（共有リングのスロット再利用の模擬）。</summary>
        public void Fill(Surface surface, Color4 color)
        {
            Gpu.Context.ClearRenderTargetView(surface.Target!, color);
            Gpu.Fence.Wait("test.fill");
        }

        public Color4 ReadCenterPixel(TargetSurface target)
        {
            if (staging == null || stagingWidth != target.Width || stagingHeight != target.Height)
            {
                staging?.Dispose();
                staging = Gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)target.Width,
                    Height = (uint)target.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
                stagingWidth = target.Width;
                stagingHeight = target.Height;
            }

            Gpu.Context.CopyResource(staging, target.Surface.Texture);
            var mapped = Gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                IntPtr center = IntPtr.Add(mapped.DataPointer,
                    (int)((target.Height / 2) * mapped.RowPitch + (target.Width / 2) * 4));
                byte b = Marshal.ReadByte(center, 0);
                byte g = Marshal.ReadByte(center, 1);
                byte r = Marshal.ReadByte(center, 2);
                byte a = Marshal.ReadByte(center, 3);
                return new Color4(r / 255f, g / 255f, b / 255f, a / 255f);
            }
            finally
            {
                Gpu.Context.Unmap(staging, 0);
            }
        }

        public void Dispose()
        {
            staging?.Dispose();
            Shaders.Dispose();
            Gpu.Dispose();
        }
    }
}
