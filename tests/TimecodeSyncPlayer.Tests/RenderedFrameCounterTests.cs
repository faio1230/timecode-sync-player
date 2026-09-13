namespace TimecodeSyncPlayer.Tests;

using System.IO;
using FluentAssertions;

/// <summary>
/// D4: ロード安定ゲートのフレーム数供給元。GPU 合成では CPU のビットマップ描画数が
/// 0 のままなので、OutputEngine の公開数を選ぶことを固定する。
/// </summary>
public class RenderedFrameCounterTests
{
    [Fact]
    public void Read_GpuCompositing_UsesPublishedFrames_NotCpuBitmaps()
    {
        long cpuRenderedFrames = 0;
        long publishedFrames = 0;
        var counter = new RenderedFrameCounter(
            gpuCompositing: true,
            cpuRenderedFrames: () => cpuRenderedFrames,
            gpuPublishedFrames: () => publishedFrames);

        counter.Read().Should().Be(0);

        publishedFrames = 7;                 // 合成プールへ 7 フレーム公開
        counter.Read().Should().Be(7);

        cpuRenderedFrames = 99;               // CPU 側が動いても GPU 合成では参照しない
        counter.Read().Should().Be(7);
    }

    [Fact]
    public void Read_CpuCompositing_UsesCpuRenderedFrames()
    {
        var counter = new RenderedFrameCounter(
            gpuCompositing: false,
            cpuRenderedFrames: () => 5,
            gpuPublishedFrames: () => 99);

        counter.Read().Should().Be(5);
    }

    [Fact]
    public void MainWindow_WiresSyncGateToActiveOutputFrameCount()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "TimecodeSyncPlayer", "MainWindow.xaml.cs"));
        string source = File.ReadAllText(sourcePath);

        source.Should().NotContain(
            "GetTotalRenderedFrames: () => _playbackPerformanceStats.TotalRenderedFrames",
            "GPU 合成では WriteableBitmap を描かないため、CPU の描画数だけではゲートが 5 秒開かない");
        source.Should().Contain("GetTotalRenderedFrames: () => _syncGateRenderedFrames.Read()");
    }
}
