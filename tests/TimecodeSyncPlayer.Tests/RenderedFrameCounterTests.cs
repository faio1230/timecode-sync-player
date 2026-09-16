namespace TimecodeSyncPlayer.Tests;

using System.IO;
using FluentAssertions;

/// <summary>
/// D4: ロード安定ゲートのフレーム数供給元。CPU 合成の除去後は
/// OutputEngine の公開数（合成プールへ公開した数）だけを読む。
/// </summary>
public class RenderedFrameCounterTests
{
    [Fact]
    public void Read_UsesPublishedFrames()
    {
        long publishedFrames = 0;
        var counter = new RenderedFrameCounter(gpuPublishedFrames: () => publishedFrames);

        counter.Read().Should().Be(0);

        publishedFrames = 7;
        counter.Read().Should().Be(7);
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
