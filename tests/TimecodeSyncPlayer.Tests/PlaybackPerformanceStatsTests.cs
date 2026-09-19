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

    [Fact]
    public void WindowGeneration_AdvancesOnEveryNewWindow_WithOrWithoutASnapshot()
    {
        // 0.4.7: 「デコードが追いついていない」判定は、窓が始まった時点の基準（速度の積分・乱れの数）を
        // 取り直す必要がある。窓は snapshot と同時にも、位置が戻ったときに黙っても作り直される。
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        long g0 = stats.WindowGeneration;

        stats.RecordTick(10.0, start);
        stats.WindowGeneration.Should().Be(g0 + 1, "最初の tick で窓が始まる");

        stats.RecordTick(11.0, start.AddSeconds(1));
        stats.WindowGeneration.Should().Be(g0 + 1, "窓の途中では変わらない");

        stats.RecordTick(10.6, start.AddSeconds(1.5)).Should().BeNull();
        stats.WindowGeneration.Should().Be(g0 + 2, "位置が戻ると snapshot 無しで作り直される");

        stats.RecordTick(12.6, start.AddSeconds(3.5)).Should().NotBeNull();
        stats.WindowGeneration.Should().Be(g0 + 3, "snapshot を返すと同時に次の窓が始まる");
    }
}
