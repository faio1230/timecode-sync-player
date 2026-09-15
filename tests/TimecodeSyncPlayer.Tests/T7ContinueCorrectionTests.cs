using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T7: 同期補正（Smooth / Jump）の残差とシーク先を Continue モードの素材位置へ直す。
/// 残差 = 素材位置 − 再生位置（同じフレームの sync.evaluate delta と一致）、
/// Jump のシーク先 = 素材位置。補正を評価しない状況と、トラック切替での Smooth
/// 再有効化（Reset）も固定する。OutputTrace.Current を差し替えるため直列コレクション。
/// </summary>
[Collection("OutputTrace")]
public class T7ContinueCorrectionTests
{
    // ── LtcSyncController + シナリオ基盤 ─────────────────────────────

    private static SyncScenarioHarness ArrangeClip2OnTrack(double playbackSeconds)
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();
        harness.SupplyLtc(12.0);                                  // clip2 へ切替（素材 0.0 から）
        harness.AdvancePlayback(playbackSeconds, renderedFrames: 2);  // ロード進捗を満たす
        return harness;
    }

    [Fact]
    public void ContinueClip2_Smooth_ResidualIsMediaPositionMinusPlayback()
    {
        SyncScenarioHarness harness = ArrangeClip2OnTrack(playbackSeconds: 0.450);
        harness.AppliedRates.Clear();

        harness.SupplyLtc(12.5);                                  // 素材位置 0.500

        // 残差は 0.500 - 0.450 = +50ms（12050ms ではない）
        harness.AppliedRates.Should().ContainSingle();
        harness.AppliedRates[0].Should().BeApproximately(1.05, 1e-9);
        harness.CorrectionStatus.Should().Be("");
    }

    [Fact]
    public void ContinueClip2_Jump_SeeksToMediaPosition()
    {
        SyncScenarioHarness harness = ArrangeClip2OnTrack(playbackSeconds: 0.450);
        harness.CorrectionMode = SyncCorrectionMode.Jump;
        harness.Operations.Clear();

        harness.SupplyLtc(12.5);

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Smooth_ReenabledAfterTrackSwitch()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();

        harness.SupplyLtc(1.0);                                   // clip1 へ切替（素材 1.0 から）
        harness.AdvancePlayback(1.1, renderedFrames: 2);

        // 残差 +100ms を維持したまま 2 秒以上経過させ、Smooth を smooth-ineffective にする。
        for (int i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            harness.SupplyLtc(2.0);                               // 素材位置 2.0
            harness.AdvancePlayback(1.9, renderedFrames: 1);      // 残差 +100ms
        }
        harness.CorrectionStatus.Should().Contain("効かない");

        harness.SupplyLtc(12.0);                                  // clip2 へ切替 → Reset
        harness.AdvancePlayback(0.45, renderedFrames: 2);
        harness.AppliedRates.Clear();

        harness.SupplyLtc(12.5);                                  // 素材 0.5、残差 +50ms

        harness.AppliedRates.Should().ContainSingle();
        harness.AppliedRates[0].Should().BeApproximately(1.05, 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(300.0)]
    public void ContinueClip2_CorrectionResidual_EqualsSyncEvaluateDelta(double offsetMs)
    {
        var harness = new SyncScenarioHarness(enableCorrection: true)
        {
            SyncOffsetMilliseconds = offsetMs,
        };
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();

        double deltaSeconds;
        string dir = Path.Combine(Path.GetTempPath(), "tcs-t7-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trace = new OutputTrace(dir, capacity: 1000) { OriginQpc = Stopwatch.GetTimestamp() };
            OutputTrace.Current = trace;
            try
            {
                harness.SupplyLtc(12.0 - (offsetMs / 1000.0));    // effective 12.0 で切替
                harness.AdvancePlayback(0.45, renderedFrames: 2);
                harness.AppliedRates.Clear();
                harness.SupplyLtc(12.5 - (offsetMs / 1000.0));    // effective 12.5、素材 0.500
            }
            finally
            {
                OutputTrace.Current = OutputTrace.Disabled;
            }

            trace.Save(
                new OutputTraceRunSummary("completed", false, false, 16, 16, 3, 3, "test", 0, 0, 0, null, false, null),
                new LatestPool(3),
                null);
            var evaluate = File.ReadAllLines(Path.Combine(dir, "events.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(e => e.GetProperty("stage").GetString() == "sync.evaluate")
                .Should().ContainSingle().Subject;
            string detail = evaluate.GetProperty("detail").GetString()!;
            deltaSeconds = double.Parse(
                detail.Split("delta=", StringSplitOptions.None)[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                CultureInfo.InvariantCulture);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }

        harness.AppliedRates.Should().ContainSingle();
        (harness.AppliedRates[0] - 1.0).Should().BeApproximately(deltaSeconds, 1e-6);
    }

    [Fact]
    public void SwitchTrackFrame_DoesNotEvaluateCorrection()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();

        harness.SupplyLtc(12.0);                                  // 切替フレーム

        harness.AppliedRates.Should().BeEmpty();
    }

    [Fact]
    public void GapFrame_DoesNotEvaluateCorrection()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 8);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);                                   // clip1 へ切替
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.AppliedRates.Clear();

        harness.SupplyLtc(6.0);                                   // timeline 6 はギャップ

        harness.AppliedRates.Should().BeEmpty();
    }

    [Fact]
    public void SuppressedSyncFrame_DropsPreviousFrameContext()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);                                   // clip1 へ切替
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // 残差 +0.1 → 文脈が入る
        harness.Controller.LastContinueFrame.Should().NotBeNull();

        // 信号断 → ポリシーが停止して ShouldSuppressSync=true になる。
        harness.Tick100Milliseconds(4);
        harness.IsPaused.Should().BeTrue();
        harness.Controller.LastContinueFrame.Should().NotBeNull("セットアップ: tick では文脈を捨てない");
        harness.AppliedRates.Clear();

        harness.SupplyLtc(1.6);                                   // 抑止フレーム（RequestSync が走らない）

        harness.Controller.LastContinueFrame.Should().BeNull(
            "抑止フレームでは前のフレームの素材位置・再生位置を使わない");
        harness.AppliedRates.Should().BeEmpty();
    }

    [Fact]
    public void SmoothRateRestoredOnTrackSwitch()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);

        harness.SupplyLtc(12.0);                                  // clip2 へ切替

        harness.AppliedRates.Should().HaveCount(2);
        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void SmoothRateRestoredOnGapEnter()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 8);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);

        harness.SupplyLtc(6.0);                                   // ギャップ入り

        harness.AppliedRates.Should().HaveCount(2);
        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void SmoothRateRestoredOnManualControl()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10

        harness.Controller.CorrectionReset();                     // 手動の再生・一時停止/差し替え相当
        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);

        harness.AdvancePlayback(1.3, renderedFrames: 1);
        harness.SupplyLtc(1.4);                                   // rate 1.10 に戻す
        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);

        harness.BeginSeekBarInteraction();                        // 手動シーク

        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void SmoothRateRestore_RetriedOnNextEvaluableFrame()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates.Should().ContainSingle();

        harness.RateApplySucceeds = false;
        harness.Controller.CorrectionReset();                     // 一時停止中相当で戻せない
        harness.AppliedRates.Should().ContainSingle("拒否された戻しは成功に数えない");
        harness.RateAttempts[^1].Should().BeApproximately(1.0, 1e-9);

        harness.RateApplySucceeds = true;
        harness.AdvancePlayback(1.25, renderedFrames: 1);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        harness.SupplyLtc(1.3);                                   // 評価の前に 1.0 へ戻す

        harness.AppliedRates.Should().HaveCount(3);
        harness.AppliedRates[1].Should().BeApproximately(1.0, 1e-9);
        harness.AppliedRates[2].Should().BeApproximately(1.05, 1e-9);
    }

    // ── ContinueOnTrackCoordinator のフレーム文脈 ─────────────────────

    private sealed class Recorder
    {
        public bool LoadFileResult = true;
        public bool NativeSeeking;
        public Guid? LoadedTrackId;
        public long TotalRenderedFrames;
        public (int rc, double playbackSeconds) TimePos = (0, 0.45);
        public GapExitActionType GapExit = GapExitActionType.None;
        public Func<double, SyncPlaybackState> BuildState = playback =>
            new(true, true, false, playback, 5.0, 25.0, 25.0);

        public ContinueOnTrackEffects Build() => new(
            PeekGapExit: () => new GapExitAction(GapExit),
            IsPlaybackPaused: () => false,
            ClearGapFreezeFrame: () => { },
            DecideGapExit: () => new GapExitAction(GapExit),
            SeekTo: _ => true,
            ResumeMpvPause: () => { },
            ApplyPauseState: _ => { },
            ShowOsdBar: () => { },
            UpdateCurrentTrackLabel: () => { },
            GetLoadedTrackId: () => LoadedTrackId,
            SetLoadedTrackId: id => LoadedTrackId = id,
            LoadFile: (_, _) => LoadFileResult,
            GetTotalRenderedFrames: () => TotalRenderedFrames,
            GetTimePos: () => TimePos,
            BuildPlaybackState: BuildState,
            IsNativeSeeking: () => NativeSeeking);
    }

    private static PlaylistTrack Track() => new(
        Guid.NewGuid(), "C:/clip.mp4", "track", TimeSpan.Zero, null, TimeSpan.Zero,
        TimeSpan.FromSeconds(5), TimeSpan.Zero, 25, true);

    private static TimelineQueryResult OnTrack(PlaylistTrack track, double mediaPos) =>
        new(TimelineQueryStatus.OnTrack, track, mediaPos, null);

    private static TimecodeSyncService Service() => new(new SyncDecisionEngine(), new TimecodeSyncSeekState());

    private static ContinueOnTrackCoordinator Coordinator(Recorder recorder, TimecodeSyncService? service = null) =>
        new(service ?? Service(), new FileLoadStabilityLogState(TimeSpan.FromSeconds(1)), recorder.Build());

    [Fact]
    public void FrameContext_ContinueCurrentTrack_CarriesMediaPositionAndPlayback()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.45) };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 0.5), 12.5);

        frame.Request.Should().Be(SyncRequestResult.Complete);
        frame.CorrectionAllowed.Should().BeTrue();
        frame.MediaPositionSeconds.Should().BeApproximately(0.5, 1e-9);
        frame.PlaybackSeconds.Should().BeApproximately(0.45, 1e-9);
    }

    [Fact]
    public void FrameContext_SwitchTrack_BlocksAndMarksSwitch()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = Guid.NewGuid() };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 12.5), 12.5);

        frame.Request.Should().Be(SyncRequestResult.Complete);
        frame.SwitchedTrack.Should().BeTrue();
        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("switch");
    }

    [Fact]
    public void FrameContext_SwitchTrackFailure_Blocks()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = Guid.NewGuid(), LoadFileResult = false };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 12.5), 12.5);

        frame.Request.Should().Be(SyncRequestResult.Deferred);
        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("switch-failed");
    }

    [Fact]
    public void FrameContext_NativeSeeking_Blocks()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, NativeSeeking = true };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 0.5), 12.5);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("native-seeking");
    }

    [Fact]
    public void FrameContext_TimePosFailure_Blocks()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (1, 0.0) };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 0.5), 12.5);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("time-pos");
    }

    [Fact]
    public void FrameContext_LoadStabilityWait_Blocks()
    {
        var track = Track();
        var service = Service();
        service.BeginFileLoad(0.5, 100);
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.5), TotalRenderedFrames = 100 };

        ContinueFrameContext frame = Coordinator(recorder, service).HandleFrame(OnTrack(track, 100.0), 100.0);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("load-stability");
    }

    [Fact]
    public void FrameContext_SeekIssued_Blocks()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.0) };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 1.0), 1.0);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("seek-issued");
    }

    [Fact]
    public void FrameContext_GapExitSeek_BlocksAndMarksGapExit()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, GapExit = GapExitActionType.ResumePlayback };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 0.5), 12.5);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.ExitedGap.Should().BeTrue();
        frame.CorrectionBlockedReason.Should().Be("gap-exit");
    }
}
