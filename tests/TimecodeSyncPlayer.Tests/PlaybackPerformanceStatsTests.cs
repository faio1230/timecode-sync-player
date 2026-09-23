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
    public void RecordTick_KeepsPeriodicSnapshotsAcrossBackwardJumps()
    {
        // 0.4.8: 位置が戻っても窓を作り直さない。以前は戻りが 2 秒以内に続くと snapshot が出ず、
        // 性能ログが 20 秒以上途切れた（UIA 50ms 監査の失敗）。戻りは回数と最大幅で別に数える。
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(100.0, start).Should().BeNull();
        var snapshots = new List<PlaybackPerformanceSnapshot>();
        // 0.25 秒ごとに +0.5 秒進んで -0.3 秒戻る、を 10 秒続ける（正味 +0.2 秒 / 0.25 秒 = 0.8 倍速）。
        double position = 100.0;
        for (int i = 1; i <= 40; i++)
        {
            DateTime now = start.AddSeconds(i * 0.25);
            position += 0.5;
            if (stats.RecordTick(position, now.AddMilliseconds(-1)) is { } s1) snapshots.Add(s1);
            position -= 0.3;
            if (stats.RecordTick(position, now) is { } s2) snapshots.Add(s2);
        }

        snapshots.Should().HaveCount(5, "10 秒の間、2 秒ごとに必ず出る");
        snapshots.Should().OnlyContain(s => s.BackwardJumps >= 7);
        snapshots.Should().OnlyContain(s => Math.Abs(s.MaxBackwardSeconds - 0.3) < 1e-9);
        // 速度は「進んだ量の合計 / 経過」。戻りの量は差し引かない（戻りは別に数えている）。
        snapshots[1].PlaybackRate.Should().BeApproximately(2.0, 0.1);
    }

    [Fact]
    public void RecordTick_ReportsNoBackwardJumpsWhenPositionOnlyAdvances()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        stats.RecordTick(10.0, start);
        stats.RecordTick(11.0, start.AddSeconds(1));
        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(12.0, start.AddSeconds(2));

        snapshot.Should().NotBeNull();
        snapshot!.BackwardJumps.Should().Be(0);
        snapshot.MaxBackwardSeconds.Should().Be(0);
        snapshot.PlaybackRate.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void RecordTick_UsesTheGivenMonotonicTime()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        TimeSpan t0 = TimeSpan.FromSeconds(5000);

        stats.RecordTick(1.0, t0).Should().BeNull();
        stats.RecordTick(2.9, t0 + TimeSpan.FromSeconds(1.9)).Should().BeNull();
        stats.RecordTick(3.0, t0 + TimeSpan.FromSeconds(2.0)).Should().NotBeNull();
    }

    [Fact]
    public void WindowGeneration_AdvancesOnlyWhenANewWindowStarts()
    {
        // 0.4.7: 「デコードが追いついていない」判定は、窓が始まった時点の基準（速度の積分・乱れの数）を
        // 取り直す必要がある。0.4.8: 位置の戻りでは窓が始まらないので、世代も進まない。
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        long g0 = stats.WindowGeneration;

        stats.RecordTick(10.0, start);
        stats.WindowGeneration.Should().Be(g0 + 1, "最初の tick で窓が始まる");

        stats.RecordTick(11.0, start.AddSeconds(1));
        stats.WindowGeneration.Should().Be(g0 + 1, "窓の途中では変わらない");

        stats.RecordTick(10.6, start.AddSeconds(1.5)).Should().BeNull();
        stats.WindowGeneration.Should().Be(g0 + 1, "位置が戻っても窓は作り直さない");

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(11.1, start.AddSeconds(2));
        snapshot.Should().NotBeNull();
        snapshot!.BackwardJumps.Should().Be(1);
        stats.WindowGeneration.Should().Be(g0 + 2, "snapshot を返すと同時に次の窓が始まる");
    }

    [Fact]
    public void RecordTick_BackwardJumpAfterAnOperation_StartsANewWindow()
    {
        // 0.4.8 候補 2: シークなどの操作をまたいで位置が戻ったら、従来どおり窓を作り直す。
        // 候補 1 では窓を保ったため、参照採取の後の先頭へのシークをまたいで 6.4 秒・0.23 倍の窓ができた。
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        stats.RecordTick(10.0, start, operationEpoch: 5);
        stats.RecordTick(11.0, start.AddSeconds(1), operationEpoch: 5);
        long generation = stats.WindowGeneration;

        // 操作（epoch 6）の後に 1.36 秒戻った。
        stats.RecordTick(9.64, start.AddSeconds(1.5), operationEpoch: 6).Should().BeNull();
        stats.WindowGeneration.Should().Be(generation + 1, "操作をまたいだ戻りでは窓を作り直す");

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(11.64, start.AddSeconds(3.5), operationEpoch: 6);
        snapshot.Should().NotBeNull();
        snapshot!.Elapsed.Should().Be(TimeSpan.FromSeconds(2));
        snapshot.PlaybackRate.Should().BeApproximately(1.0, 0.0001);
        snapshot.BackwardJumps.Should().Be(0);
    }

    [Fact]
    public void RecordTick_ForwardJumpAfterAnOperation_AlsoStartsANewWindow()
    {
        // 0.4.8 以降: 読み込みと先頭の暗転をまたいで位置が前へ進んだ窓（6.2 秒・1.6 倍・フレーム不足 5.15 秒）が
        // 検証機の HAP の L-2 で出た。操作をまたいだら、向きに関係なく作り直す。
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(2));
        DateTime start = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        stats.RecordTick(0.5, start, operationEpoch: 1);
        long generation = stats.WindowGeneration;

        stats.RecordTick(0.6, start.AddSeconds(5.0), operationEpoch: 2).Should().BeNull("読み込みの後の最初の tick");
        stats.WindowGeneration.Should().Be(generation + 1);

        PlaybackPerformanceSnapshot? snapshot = stats.RecordTick(2.6, start.AddSeconds(7.0), operationEpoch: 2);
        snapshot.Should().NotBeNull();
        snapshot!.Elapsed.Should().Be(TimeSpan.FromSeconds(2));
        snapshot.PlaybackRate.Should().BeApproximately(1.0, 0.0001);
    }
}
