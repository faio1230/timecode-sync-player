using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackPerformanceWarningPolicyTests
{
    [Fact]
    public void ShouldWarnDisplayedFps_ReturnsTrue_WhenDisplayedFpsIsBelowThreshold()
    {
        var snapshot = Snapshot(displayedFps: 40.0, playbackRate: 1.0, renderedFrames: 10);

        bool result = PlaybackPerformanceWarningPolicy.ShouldWarnDisplayedFps(
            snapshot,
            expectedFps: 60.0);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldWarnDisplayedFps_ReturnsFalse_WhenNoSourceFps()
    {
        var snapshot = Snapshot(displayedFps: 10.0, playbackRate: 1.0, renderedFrames: 10);

        bool result = PlaybackPerformanceWarningPolicy.ShouldWarnDisplayedFps(
            snapshot,
            expectedFps: 0.0);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldWarnDisplayedFps_ReturnsFalse_WhenNoRenderedFrames()
    {
        var snapshot = Snapshot(displayedFps: 10.0, playbackRate: 1.0, renderedFrames: 0);

        bool result = PlaybackPerformanceWarningPolicy.ShouldWarnDisplayedFps(
            snapshot,
            expectedFps: 60.0);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldWarnPlaybackRate_ReturnsTrue_WhenClockIsSlow()
    {
        var snapshot = Snapshot(displayedFps: 60.0, playbackRate: 0.9, renderedFrames: 10);

        PlaybackPerformanceWarningPolicy.ShouldWarnPlaybackRate(snapshot).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.95)]
    [InlineData(1.0)]
    public void ShouldWarnPlaybackRate_ReturnsFalse_WhenRateIsNotSlow(double playbackRate)
    {
        var snapshot = Snapshot(displayedFps: 60.0, playbackRate: playbackRate, renderedFrames: 10);

        PlaybackPerformanceWarningPolicy.ShouldWarnPlaybackRate(snapshot).Should().BeFalse();
    }

    private static PlaybackPerformanceSnapshot Snapshot(double displayedFps, double playbackRate, int renderedFrames) =>
        new(
            Elapsed: TimeSpan.FromSeconds(2),
            TickCount: 2,
            RenderUpdates: 2,
            FrameUpdates: 2,
            RenderedFrames: renderedFrames,
            PlaybackRate: playbackRate,
            DisplayedFps: displayedFps,
            AvgRenderMs: 1.0,
            MaxRenderMs: 2.0,
            AvgBitmapMs: 1.0,
            MaxBitmapMs: 2.0,
            AvgSpoutMs: 1.0,
            MaxSpoutMs: 2.0,
            Width: 1920,
            Height: 1080,
            SpoutEnabled: false);
}
