using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D30: 誤デコードの単発 Jump をそのまま適用しない。写像がギャップ（先頭オフセットを含む）か
/// 現在と別トラックになる Jump は、次の 1 フレームで値の連続（同値の Duplicate または +1 フレーム）
/// を確認してから適用する。同一トラック内の Jump は従来どおり即時。Fixed fps モードでデコーダ
/// 推定 fps が解決 fps と食い違う Jump も未確認扱い。保持損失からの復帰も確認済み Jump に限る。
/// </summary>
public sealed class LtcJumpConfirmationTests
{
    private static void Raw(SyncScenarioHarness h, double seconds, long at, double detectedFps = 25.0)
    {
        int frame = (int)Math.Round(seconds * 25.0);
        var timecode = new LtcTimecode(
            frame / (25 * 3600), (frame / (25 * 60)) % 60, (frame / 25) % 60, frame % 25, false);
        h.Controller.ReceiveFrame(new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), at);
    }

    /// <summary>
    /// A（タイムライン 5〜25）をロード済みで 7.0 まで通常進行している状態。
    /// 同期シークのデバウンス（250ms）とセットル後の抑止（500ms）を明けておき、
    /// 次の Jump が適用されれば必ずシーク・ロードが出るようにする。
    /// </summary>
    private static (SyncScenarioHarness Harness, PlaylistTrack A, PlaylistTrack B, ManualTimeProvider Clock)
        ArrangeContinueWithA()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock) { GapBehavior = GapBehavior.Black };
        PlaylistTrack a = h.AddTrack("A", 5, 20);
        PlaylistTrack b = h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ManualPlay();
        Raw(h, 7.0, 10_000);
        Raw(h, 7.04, 10_040);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Raw(h, 7.08, 10_080);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        h.Operations.Clear();
        return (h, a, b, clock);
    }

    [Fact]
    public void GapMappingJump_IsNotAppliedUntilTheNextFrameConfirms()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 0.04, 10_080);

        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile" || o.Name == "pause-for-gap");
        h.IsGapActive.Should().BeFalse("未確認の Jump ではギャップへ入らない");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Video);

        Raw(h, 0.08, 10_120);

        h.IsGapActive.Should().BeTrue("+1 フレームの連続で確認できた Jump は適用する");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void GapMappingJump_SameValueDuplicateConfirms()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 0.04, 10_080);
        h.IsGapActive.Should().BeFalse();

        Raw(h, 0.04, 10_120);

        h.IsGapActive.Should().BeTrue("同値の Duplicate も確認として扱う");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void OtherTrackJump_IsDeferredThenAppliedOnContinuation()
    {
        (SyncScenarioHarness h, _, PlaylistTrack b, _) = ArrangeContinueWithA();

        Raw(h, 40.0, 10_080);

        h.Operations.Should().NotContain(o => o.Name == "loadfile" || o.Name == "seek", "未確認の Jump では切り替えない");
        h.LoadedTrackId.Should().NotBe(b.Id);

        Raw(h, 40.04, 10_120);

        h.Operations.Should().Contain(o => o.Name == "loadfile", "確認できた Jump は適用する");
        h.LoadedTrackId.Should().Be(b.Id);
    }

    [Fact]
    public void SameTrackJump_IsAppliedImmediately()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 12.0, 10_080);

        h.Operations.Should().Contain(o => o.Name == "seek", "同一トラック内の Jump は確認を待たない");
        h.IsGapActive.Should().BeFalse();
    }

    [Fact]
    public void FixedModeJumpWithMismatchedDetectedFps_IsDeferredEvenWithinTheTrack()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 12.0, 10_080, detectedFps: 24.0);

        h.Operations.Should().NotContain(o => o.Name == "seek", "fps 推定の食い違いは未確認扱い");

        Raw(h, 12.04, 10_120, detectedFps: 25.0);

        h.Operations.Should().Contain(o => o.Name == "seek", "次フレームが連続すれば適用する");
    }

    [Fact]
    public void HeldLossJump_RecoversOnlyAfterConfirmation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 5, 20);
        PlaylistTrack b = h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ManualPlay();
        Raw(h, 7.0, 10_000);
        Raw(h, 7.04, 10_040);
        Raw(h, 7.04, 10_200);
        Raw(h, 7.04, 10_300);
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }

        h.IsPaused.Should().BeTrue("保持の損失で停止する");
        h.Operations.Clear();

        Raw(h, 40.0, 10_540);

        h.IsPaused.Should().BeTrue("未確認の Jump 1 枚では保持損失から復帰しない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume" || o.Name == "loadfile");

        Raw(h, 40.04, 10_580);

        h.IsPaused.Should().BeFalse("確認済みの Jump で復帰する");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
        h.Operations.Should().Contain(o => o.Name == "loadfile");
        h.LoadedTrackId.Should().Be(b.Id);
    }
}
