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

    /// <summary>
    /// v0.6.1 段 A: LTC は実時間どおり 40ms ごとに +1 フレーム送る（同じ値の 2 枚は保持、2.5 フレームを超えて先の値は
    /// Jump の保留になり、どちらも補正を評価しない）。firstLtc で読み込みに入り、読み込みの間に LTC が
    /// waitingFrames フレーム進む（着地待ちで評価しない。0 なら即時に着地）。着地（配信 = 読み込みの開始位置）の後、
    /// 着地を観測したサンプル（B6b 規則 3: 補正しない。残差 = (waitingFrames + 1) フレーム）を送る。次のフレーム
    /// （最初に補正を評価するフレーム）の LTC は firstLtc + (waitingFrames + 2) フレーム。評価のフレームの残差は、
    /// 1 フレームの間に物理的に動ける量の中で決める（残差ゲートが弾かない）。
    /// </summary>
    private static void LoadAndConsumeLandingSample(SyncScenarioHarness harness, double firstLtc, int waitingFrames)
    {
        harness.Playback.LoadDurationSeconds = waitingFrames > 0 ? 0.1 : 0;
        for (int i = 0; i <= waitingFrames; i++)
            harness.SupplyLtc(firstLtc + 0.04 * i);               // 読み込み・着地待ち
        if (waitingFrames > 0)
            harness.Playback.AdvanceTime(TimeSpan.FromMilliseconds(100));  // 読み込みの着地
        harness.Playback.LoadDurationSeconds = 0;
        harness.SupplyLtc(firstLtc + 0.04 * (waitingFrames + 1)); // 着地を観測したサンプル（補正しない）
    }

    /// <summary>v0.6.1 段 A: 位置を飛ばす LTC（Jump の値と、それを確認する +1 フレーム）。</summary>
    private static void JumpLtc(SyncScenarioHarness harness, double seconds)
    {
        harness.SupplyLtc(seconds);
        harness.SupplyLtc(seconds + 0.04);
    }

    private static SyncScenarioHarness ArrangeClip2OnTrack(double firstEvaluatedLtc, int waitingFrames)
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();
        LoadAndConsumeLandingSample(harness, firstEvaluatedLtc - 0.04 * (waitingFrames + 2), waitingFrames);  // clip2 へ切替
        return harness;
    }

    [Fact]
    public void ContinueClip2_Smooth_ResidualIsMediaPositionMinusPlayback()
    {
        // v0.6.1 段 A: LTC の値は 25fps のフレーム境界（12.5 は無い）。12.52 で素材位置 0.520。
        SyncScenarioHarness harness = ArrangeClip2OnTrack(firstEvaluatedLtc: 12.52, waitingFrames: 0);
        harness.AppliedRates.Clear();

        harness.AdvancePlayback(0.470, renderedFrames: 2);
        harness.SupplyLtc(12.52);                                 // 素材位置 0.520

        // 残差は 0.520 - 0.470 = +50ms（12050ms ではない）
        harness.AppliedRates.Should().ContainSingle();
        harness.AppliedRates[0].Should().BeApproximately(1.05, 1e-9);
        harness.CorrectionStatus.Should().Be("");
    }

    [Fact]
    public void ContinueClip2_Jump_SeeksToMediaPosition()
    {
        SyncScenarioHarness harness = ArrangeClip2OnTrack(firstEvaluatedLtc: 12.6, waitingFrames: 2);
        harness.CorrectionMode = SyncCorrectionMode.Jump;
        harness.Operations.Clear();

        harness.AdvancePlayback(0.450, renderedFrames: 2);
        harness.SupplyLtc(12.6);                                  // 素材 0.600、残差 +150ms（T8 のしきい値 80ms 超）

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void Smooth_ReenabledAfterTrackSwitch()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.AddTrack("clip2", 12);
        harness.ManualPlay();

        LoadAndConsumeLandingSample(harness, 1.0, waitingFrames: 1);  // clip1 へ切替（素材 1.0 から）

        // 残差 +100ms を維持したまま 2 秒以上経過させ、Smooth を smooth-ineffective にする
        // （LTC は 40ms ごとに +1 フレーム、再生も同じだけ進む＝倍率が効かない）。
        for (int frame = 28; frame < 28 + 60; frame++)            // LTC 1.12 から 2.4 秒
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            double ltc = frame / 25.0;
            harness.AdvancePlayback(ltc - 0.1, renderedFrames: 1); // 残差 +100ms
            harness.SupplyLtc(ltc);                               // 素材位置 = LTC
        }
        harness.CorrectionStatus.Should().Contain("効かない");

        harness.SupplyLtc(12.36);                                 // clip2 へ飛ぶ（Jump の値。確認は次のフレーム）
        LoadAndConsumeLandingSample(harness, 12.40, waitingFrames: 0);  // 確認して clip2 へ切替 → Reset
        harness.AppliedRates.Clear();

        harness.AdvancePlayback(0.43, renderedFrames: 2);
        harness.SupplyLtc(12.48);                                 // 素材 0.48、残差 +50ms

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

        // effective 約 12.44 で切替（送る LTC は 25fps のフレーム境界に丸める）。v0.5.4 B6b（規則 3）: 着地直後の
        // 1 サンプルは補正しない。trace の外で消費する。評価のフレームの素材位置から 50ms 遅れた位置を配信する。
        double firstLtc = Math.Round((12.44 - (offsetMs / 1000.0)) * 25.0) / 25.0;
        LoadAndConsumeLandingSample(harness, firstLtc, waitingFrames: 0);
        harness.AppliedRates.Clear();
        double mediaSeconds = firstLtc + 0.08 + (offsetMs / 1000.0) - 12.0;
        harness.AdvancePlayback(mediaSeconds - 0.05, renderedFrames: 2);

        double deltaSeconds;
        string dir = Path.Combine(Path.GetTempPath(), "tcs-t7-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trace = new OutputTrace(dir, capacity: 1000) { OriginQpc = Stopwatch.GetTimestamp() };
            OutputTrace.Current = trace;
            try
            {
                harness.SupplyLtc(firstLtc + 0.08);               // effective 約 12.52、素材 mediaSeconds
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

        JumpLtc(harness, 6.0);                                    // timeline 6 はギャップ（Jump とその確認）

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
        harness.AdvancePlayback(1.0, renderedFrames: 2);
        harness.SupplyLtc(1.04);                                  // 次のフレーム（残差 +0.04）→ 文脈が入る
        harness.Controller.LastContinueFrame.Should().NotBeNull();

        // 信号断 → ポリシーが停止して ShouldSuppressSync=true になる。
        harness.Tick100Milliseconds(4);
        harness.IsPaused.Should().BeTrue();
        harness.Controller.LastContinueFrame.Should().NotBeNull("セットアップ: tick では文脈を捨てない");
        harness.AppliedRates.Clear();

        harness.SupplyLtc(1.08);                                  // 再開した LTC の 1 枚目＝抑止フレーム（RequestSync が走らない）

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
        LoadAndConsumeLandingSample(harness, 1.08, waitingFrames: 1);  // B6b: 着地直後の 1 サンプル（補正しない）
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);

        JumpLtc(harness, 12.0);                                   // clip2 へ切替（Jump とその確認）

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
        LoadAndConsumeLandingSample(harness, 1.08, waitingFrames: 1);  // B6b: 着地直後の 1 サンプル（補正しない）
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);

        JumpLtc(harness, 6.0);                                    // ギャップ入り（Jump とその確認）

        harness.AppliedRates.Should().HaveCount(2);
        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void SmoothRateRestoredOnManualControl()
    {
        var harness = new SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();
        LoadAndConsumeLandingSample(harness, 1.08, waitingFrames: 1);  // B6b: 着地直後の 1 サンプル（補正しない）
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10

        harness.Controller.PlayPauseToggled();                     // 手動の再生・一時停止/差し替え相当
        harness.AppliedRates[^1].Should().BeApproximately(1.0, 1e-9);

        harness.AdvancePlayback(1.14, renderedFrames: 1);
        harness.SupplyLtc(1.24);                                  // 次のフレーム、残差 +100ms: rate 1.10 に戻す
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
        LoadAndConsumeLandingSample(harness, 1.08, waitingFrames: 1);  // B6b: 着地直後の 1 サンプル（補正しない）
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.2);                                   // rate 1.10
        harness.AppliedRates.Should().ContainSingle();

        harness.RateApplySucceeds = false;
        harness.Controller.PlayPauseToggled();                     // 一時停止中相当で戻せない
        harness.AppliedRates.Should().ContainSingle("拒否された戻しは成功に数えない");
        harness.RateAttempts[^1].Should().BeApproximately(1.0, 1e-9);

        harness.RateApplySucceeds = true;
        harness.AdvancePlayback(1.19, renderedFrames: 1);
        clock.Advance(TimeSpan.FromMilliseconds(40));
        harness.SupplyLtc(1.24);                                  // 次のフレーム、残差 +50ms。評価の前に 1.0 へ戻す

        harness.AppliedRates.Should().HaveCount(3);
        harness.AppliedRates[1].Should().BeApproximately(1.0, 1e-9);
        harness.AppliedRates[2].Should().BeApproximately(1.05, 1e-9);
    }

    // ── ContinueOnTrackCoordinator のフレーム文脈 ─────────────────────

    private sealed class Recorder
    {
        public bool LoadFileResult = true;
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
            ResumePlayback: () => { },
            ApplyPauseState: _ => { },
            UpdateCurrentTrackLabel: () => { },
            GetLoadedTrackId: () => LoadedTrackId,
            SetLoadedTrackId: id => LoadedTrackId = id,
            LoadFile: (_, _) => LoadFileResult,
            GetTotalRenderedFrames: () => TotalRenderedFrames,
            ReadPosition: () => TimePos.rc == 0 ? new SyncPositionRead(true, TimePos.playbackSeconds) : SyncPositionRead.Failed,
            BuildPlaybackState: BuildState);
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
    public void FrameContext_WhileWaitingForLanding_Blocks()
    {
        // v0.5.4 段 B3: ネイティブのシーク中は着地待ち（未信頼の決定）が止める。
        var track = Track();
        var service = Service();
        service.ReportSeekSent(0.5);
        var recorder = new Recorder { LoadedTrackId = track.Id };

        ContinueFrameContext frame = Coordinator(recorder, service).HandleFrame(OnTrack(track, 0.5), 12.5);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("position-untrusted");
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
    public void FrameContext_LoadLandingWait_Blocks()
    {
        // v0.5.4 段 B3: ロードの成立（旧 門 18）は着地の事象で決まり、待っている間は未信頼の決定が止める。
        var track = Track();
        var service = Service();
        service.BeginFileLoad(0.5, 100);
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.5), TotalRenderedFrames = 100 };

        ContinueFrameContext frame = Coordinator(recorder, service).HandleFrame(OnTrack(track, 100.0), 100.0);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("position-untrusted");
    }

    [Fact]
    public void FrameContext_SeekIssued_Blocks()
    {
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.0) };

        // D37-b: シーク 1 回の実測所要（未学習は 1.0 秒）を超える不足ではシークを出す。
        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 2.0), 2.0);

        frame.CorrectionAllowed.Should().BeFalse();
        frame.CorrectionBlockedReason.Should().Be("seek-issued");
    }

    [Fact]
    public void FrameContext_DeficitWithinTheThreshold_AllowsCorrection()
    {
        // D37-b: relocate の閾値以内の不足はシークではなく速度補正に任せる（補正は評価してよい）。
        // v0.5.4 B6b（追補 3）: 閾値は max(tol, r_max × c)。学習前は tol（0.24）なので 0.2 秒で確かめる
        // （旧は既定のシーク所要 1.0 秒以内の 0.5 秒）。
        var track = Track();
        var recorder = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 0.0) };

        ContinueFrameContext frame = Coordinator(recorder).HandleFrame(OnTrack(track, 0.2), 0.2);

        frame.CorrectionAllowed.Should().BeTrue();
        frame.Request.Should().Be(SyncRequestResult.Complete);
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
