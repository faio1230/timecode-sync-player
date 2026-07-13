using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackPerformanceStatsTests
{
    [Fact]
    public void RecordTick_ReturnsSnapshotWithPlaybackRateAndDisplayedFps()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(10.0, start).Should().BeNull();
        for (int i = 0; i < 60; i++)
        {
            stats.RecordRenderUpdate(hasFrame: true);
            stats.RecordRenderedFrame(
                renderMs: 1.0,
                bitmapMs: 2.0,
                spoutMs: 0.5,
                width: 1920,
                height: 1080,
                spoutEnabled: true);
        }

        stats.TotalRenderedFrames.Should().Be(60);

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(12.0, start.AddSeconds(2));

        snapshot.Should().NotBeNull();
        snapshot!.PlaybackRate.Should().BeApproximately(1.0, 0.0001);
        snapshot.DisplayedFps.Should().BeApproximately(30.0, 0.0001);
        snapshot.RenderUpdates.Should().Be(60);
        snapshot.FrameUpdates.Should().Be(60);
        snapshot.RenderedFrames.Should().Be(60);
        snapshot.AvgRenderMs.Should().BeApproximately(1.0, 0.0001);
        snapshot.AvgBitmapMs.Should().BeApproximately(2.0, 0.0001);
        snapshot.AvgSpoutMs.Should().BeApproximately(0.5, 0.0001);
        snapshot.Width.Should().Be(1920);
        snapshot.Height.Should().Be(1080);
        snapshot.SpoutEnabled.Should().BeTrue();
    }

    [Fact]
    public void RecordRenderUpdate_CountsCallbackWithoutFrameSeparately()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(1));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(0.0, start).Should().BeNull();
        stats.RecordRenderUpdate(hasFrame: false);
        stats.RecordRenderUpdate(hasFrame: true);
        stats.RecordRenderedFrame(1.0, 1.0, 0.0, 1280, 720, spoutEnabled: false);

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(1.0, start.AddSeconds(1));

        snapshot.Should().NotBeNull();
        snapshot!.RenderUpdates.Should().Be(2);
        snapshot.FrameUpdates.Should().Be(1);
        snapshot.RenderedFrames.Should().Be(1);
        snapshot.DisplayedFps.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void Reset_ClearsCurrentWindow()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(1));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(0.0, start);
        stats.RecordRenderUpdate(hasFrame: true);
        stats.RecordRenderedFrame(1.0, 1.0, 0.0, 1280, 720, spoutEnabled: false);
        stats.Reset();

        stats.TotalRenderedFrames.Should().Be(0);

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(1.0, start.AddSeconds(1));

        snapshot.Should().BeNull();
    }

    [Fact]
    public void RecordTick_ResetsWindowWhenPlaybackPositionJumpsBack()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(238.0, start).Should().BeNull();
        stats.RecordRenderUpdate(hasFrame: true);
        stats.RecordRenderedFrame(1.0, 1.0, 0.0, 1920, 1080, spoutEnabled: false);

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(0.1, start.AddSeconds(2));

        snapshot.Should().BeNull();
        stats.RecordTick(1.1, start.AddSeconds(3)).Should().BeNull();
        PlaybackPerformanceSnapshot? nextSnapshot = stats.RecordTick(2.1, start.AddSeconds(4));

        nextSnapshot.Should().NotBeNull();
        nextSnapshot!.PlaybackRate.Should().BeApproximately(1.0, 0.0001);
    }
}
