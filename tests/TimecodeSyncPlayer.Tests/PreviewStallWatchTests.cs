using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class PreviewStallWatchTests
{
    [Fact]
    public void BeforeFirstPreview_NeverStalls()
    {
        var watch = new PreviewStallWatch(thresholdTicks: 1000);
        watch.Check(50_000).Should().BeNull();
        watch.IsStalled.Should().BeFalse();
    }

    [Fact]
    public void StallIsReportedOnceThenResumeReportsItsLength()
    {
        var watch = new PreviewStallWatch(thresholdTicks: 1000);
        watch.Shown(10_000).Should().BeNull();

        watch.Check(10_500).Should().BeNull("しきい値未満");
        watch.Check(11_200).Should().Be(1200);
        watch.Check(12_000).Should().BeNull("同じ止まりは 1 回だけ出す");
        watch.IsStalled.Should().BeTrue();

        watch.Shown(15_000).Should().Be(5000);
        watch.IsStalled.Should().BeFalse();
        watch.Shown(15_033).Should().BeNull();
    }

    [Fact]
    public void PreviewMeanLuma_BlackIsZero_WhiteIs255()
    {
        var black = new byte[960 * 540 * 4];
        OutputEngine.PreviewMeanLuma(black, 960, 540).Should().Be(0);

        var white = Enumerable.Repeat((byte)255, 960 * 540 * 4).ToArray();
        OutputEngine.PreviewMeanLuma(white, 960, 540).Should().Be(255);
    }
}
