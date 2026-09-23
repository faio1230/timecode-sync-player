using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D7-a: 同期シーク／ロードの先行補償。decide（または load 発行）から新しい世代の最初の
/// フレームが Ready になるまでの遅延 L をトラック単位で保持し、行き先だけを +L する。
/// 学習前のトラックは L=0 で現行と完全に同じ行き先になる。
/// </summary>
public class SeekLatencyCompensatorTests
{
    private static readonly Guid TrackA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TrackB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private static long Ticks(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    private static void Learn(
        SeekLatencyCompensator compensator, double latencySeconds, long sourceSequence, long decisionQpc = 1_000)
        => Learn(compensator, latencySeconds, generation: 1, sourceSequence, decisionQpc);

    private static void Learn(
        SeekLatencyCompensator compensator, double latencySeconds, int generation, long sourceSequence, long decisionQpc = 1_000)
    {
        compensator.MarkSeekDecision(decisionQpc);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(decisionQpc + Ticks(latencySeconds), generation, sourceSequence);
    }

    private static SyncPlaybackState SeekYieldingState(double playbackSeconds) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    [Fact]
    public void CompensateTarget_WithoutLearning_ReturnsCurrentTargetClampedToRange()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);

        compensator.CompensateTarget(10.0, 100.0).Should().Be(10.0);
        compensator.CompensateTarget(-5.0, 100.0).Should().Be(0.0);
        compensator.CompensateTarget(150.0, 100.0).Should().Be(100.0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData(" on ", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("true", false)]
    [InlineData("1", false)]
    public void IsCompensationEnabled_OnlyOnEnables(string? value, bool expected)
        => SeekLatencyCompensator.IsCompensationEnabled(value).Should().Be(expected);

    [Fact]
    public void CompensationDisabled_KeepsLZeroEvenAfterObservations()
    {
        var compensator = new SeekLatencyCompensator(enabled: false);
        compensator.SelectTrack(TrackA);

        Learn(compensator, latencySeconds: 0.3, sourceSequence: 1);

        compensator.CompensationSeconds.Should().Be(0.0);
        compensator.CompensationForTrack(TrackA).Should().Be(0.0);
        compensator.CompensateTarget(10.0, 100.0).Should().Be(10.0);
        compensator.IsMeasurementArmed.Should().BeFalse();
    }

    /// <summary>D37-a: 粗い判定は 3 サンプル（250ms 窓）そろってから Seek を出す。</summary>
    private static SyncDecision DecideAfterGate(SyncDecisionEngine engine, double ltcSeconds, SyncPlaybackState state)
    {
        SyncDecision decision = engine.Decide(ltcSeconds, state);
        for (int i = 0; i < 2; i++)
            decision = engine.Decide(ltcSeconds, state);
        return decision;
    }

    [Fact]
    public void CompensationDisabled_EngineKeepsCurrentTargetAndDelta()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), new SeekLatencyCompensator(enabled: false));

        SyncDecision decision = DecideAfterGate(engine, 10.0, SeekYieldingState(4.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(10.0);
        decision.DeltaSeconds.Should().Be(6.0);
    }

    [Fact]
    public void CompensationDisabled_ServiceDoesNotArmMeasurement()
    {
        var compensator = new SeekLatencyCompensator(enabled: false);
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);
        var service = new TimecodeSyncService(engine, new TimecodeSyncSeekState(), null, compensator);

        SyncDecision decision = service.EvaluateDecision(10.0, SeekYieldingState(4.0));
        service.ReportSeekSent(decision.TargetSeconds);

        compensator.IsMeasurementArmed.Should().BeFalse();
        compensator.CompensationSeconds.Should().Be(0.0);
    }

    [Fact]
    public void CompensateTarget_FirstSamplePerTrack_IsAdoptedWithoutEma()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);

        Learn(compensator, latencySeconds: 0.3, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.3, 1e-9);
        compensator.CompensateTarget(10.0, 100.0).Should().BeApproximately(10.3, 1e-9);
    }

    [Fact]
    public void ObserveFrameReady_SecondSampleUsesEma()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.3, sourceSequence: 1);

        Learn(compensator, latencySeconds: 0.1, sourceSequence: 2);

        compensator.CompensationSeconds.Should().BeApproximately(
            0.3 + (SeekLatencyCompensator.EmaAlpha * (0.1 - 0.3)), 1e-9);
    }

    [Fact]
    public void CompensateTarget_ClampsToDurationAndZero()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1);

        compensator.CompensateTarget(99.99, 100.0).Should().Be(100.0);
        compensator.CompensateTarget(-0.3, 100.0).Should().Be(0.0);
    }

    [Fact]
    public void ObserveFrameReady_ClampsCompensationAtUpperBound()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        for (int i = 1; i <= 10; i++)
            Learn(compensator, latencySeconds: 1.0, sourceSequence: i, decisionQpc: i * 10_000);

        compensator.CompensationSeconds.Should().Be(SeekLatencyCompensator.MaxCompensationSeconds);
        compensator.CompensateTarget(10.0, 100.0).Should().BeApproximately(10.4, 1e-9);
    }

    [Fact]
    public void TrackSwitch_KeepsLearnedCompensation()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.24, sourceSequence: 1);

        compensator.SelectTrack(TrackB);
        compensator.CompensationSeconds.Should().Be(0.0); // B は未学習

        compensator.SelectTrack(TrackA);
        compensator.CompensationSeconds.Should().BeApproximately(0.24, 1e-9);
    }

    [Fact]
    public void TrackSwitch_DoesNotMixTrackCompensations()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.24, sourceSequence: 1);

        compensator.SelectTrack(TrackB);
        Learn(compensator, latencySeconds: 0.1, sourceSequence: 2);

        compensator.CompensationForTrack(TrackA).Should().BeApproximately(0.24, 1e-9);
        compensator.CompensationForTrack(TrackB).Should().BeApproximately(0.1, 1e-9);
        compensator.CompensationSeconds.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void ObserveFrameReady_IgnoresFramesAtOrBeforeArmSequence()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        compensator.ObserveFrameReady(900, generation: 1, sourceSequence: 5);

        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(1_000 + Ticks(0.3), generation: 1, sourceSequence: 5);

        compensator.CompensationSeconds.Should().Be(0.0);
        compensator.IsMeasurementArmed.Should().BeTrue();

        compensator.ObserveFrameReady(1_000 + Ticks(0.3), generation: 1, sourceSequence: 6);

        compensator.CompensationSeconds.Should().BeApproximately(0.3, 1e-9);
        compensator.IsMeasurementArmed.Should().BeFalse();
    }

    [Fact]
    public void ObserveFrameReady_AcceptsNewGenerationEvenWhenSequenceResets()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        compensator.ObserveFrameReady(900, generation: 1, sourceSequence: 10);

        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(1_000 + Ticks(0.2), generation: 2, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void Measurement_IsArmedOnlyAfterSeekIsSent()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);

        compensator.MarkSeekDecision(1_000);
        compensator.IsMeasurementArmed.Should().BeFalse();

        compensator.MarkSeekSent();
        compensator.IsMeasurementArmed.Should().BeTrue();
    }

    [Fact]
    public void MarkLoadSent_ArmsMeasurementAtIssuedQpc()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);

        compensator.MarkLoadSent(2_000);
        compensator.IsMeasurementArmed.Should().BeTrue();
        compensator.ObserveFrameReady(2_000 + Ticks(0.35), generation: 1, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.35, 1e-9);
    }

    [Fact]
    public void MarkSeekDecision_WhileArmed_KeepsTheArmedMeasurementStart()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent();

        // シーク保留中に同期判定が再評価されても、測定は発行時の決定から動かさない。
        compensator.MarkSeekDecision(5_000);
        compensator.ObserveFrameReady(1_000 + Ticks(0.2), generation: 1, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void MarkSeekSent_PromotesLatestDecisionQpc_ForTheNextSeek()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent(); // 1本目（未観測のまま2本目へ）

        compensator.MarkSeekDecision(2_000);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(2_000 + Ticks(0.3), generation: 1, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.3, 1e-9);
    }

    [Fact]
    public void MarkSeekSent_WithoutNewDecision_DoesNotReuseTheOldDecisionQpc()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent();

        // 新しい決定なしの再送は現在時刻から測る（古い 1_000 を拾うと上限クランプになる）。
        compensator.MarkSeekSent();
        long now = Stopwatch.GetTimestamp();
        compensator.ObserveFrameReady(now + Ticks(0.2), generation: 1, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(0.2, 0.01);
    }

    [Fact]
    public void Engine_WithoutLearning_KeepsCurrentTargetAndDelta()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), new SeekLatencyCompensator(enabled: true));

        SyncDecision decision = DecideAfterGate(engine, 10.0, SeekYieldingState(4.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(10.0);
        decision.DeltaSeconds.Should().Be(6.0);
    }

    [Fact]
    public void Engine_AfterLearning_AppliesCompensatedTargetButRawDelta()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1);
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);

        SyncDecision decision = DecideAfterGate(engine, 10.0, SeekYieldingState(4.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(10.2, 1e-9);
        decision.DeltaSeconds.Should().Be(6.0);
    }

    [Fact]
    public void Engine_UsesCompensationOfCurrentTrackOnly()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1);
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);

        DecideAfterGate(engine, 10.0, SeekYieldingState(4.0)).TargetSeconds.Should().BeApproximately(10.2, 1e-9);

        compensator.SelectTrack(TrackB);
        DecideAfterGate(engine, 10.0, SeekYieldingState(4.0)).TargetSeconds.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void Engine_KeepsRawErrorDecision_WhenOnlyCompensatedDeltaWouldExceedTolerance()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1); // L = 0.2
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);

        // raw delta = +0.18 <= tolerance 0.2 なので None。補償後の delta = 0.38 で判定してはいけない。
        SyncDecision decision = engine.Decide(10.0, SeekYieldingState(9.82));

        decision.Action.Should().Be(SyncActionType.None);
        decision.TargetSeconds.Should().Be(0.0);
    }

    [Fact]
    public void Engine_ClampsCompensatedTargetToDuration()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1);
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);

        SyncDecision decision = DecideAfterGate(engine, 199.99, SeekYieldingState(4.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(200.0 - 1.0 / 30.0, 1e-9, "補償後も最後のコマの頭まで");
    }

    [Fact]
    public void Service_ReportSeekSent_ArmsMeasurementAtDecisionTime()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);
        var service = new TimecodeSyncService(engine, new TimecodeSyncSeekState(), null, compensator);

        SyncDecision decision = service.EvaluateDecision(10.0, SeekYieldingState(4.0));
        decision = service.EvaluateDecision(10.0, SeekYieldingState(4.0));
        decision = service.EvaluateDecision(10.0, SeekYieldingState(4.0));
        decision.Action.Should().Be(SyncActionType.Seek);
        compensator.IsMeasurementArmed.Should().BeFalse();

        service.ReportSeekSent(decision.TargetSeconds);
        compensator.IsMeasurementArmed.Should().BeTrue();

        compensator.ObserveFrameReady(Stopwatch.GetTimestamp() + Ticks(0.2), generation: 1, sourceSequence: 1);
        // decide QPC はエンジン内で取るため、テスト側の観測時刻までに数 ms のずれが乗る。
        compensator.CompensationSeconds.Should().BeApproximately(0.2, 0.01);
    }

    [Fact]
    public void Service_BeginFileLoad_KeepsLearnedCompensationAndArmsLoadMeasurement()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.3, sourceSequence: 1);
        var service = new TimecodeSyncService(
            new SyncDecisionEngine(new SyncDecisionOptions(), compensator),
            new TimecodeSyncSeekState(), null, compensator);

        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);

        compensator.CompensationSeconds.Should().BeApproximately(0.3, 1e-9); // トラック切替で消えない
        compensator.IsMeasurementArmed.Should().BeTrue();
    }

    [Fact]
    public void Service_BeginFileLoad_MeasuresFromLoadIssuedQpc()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.3, sourceSequence: 1);
        var service = new TimecodeSyncService(
            new SyncDecisionEngine(new SyncDecisionOptions(), compensator),
            new TimecodeSyncSeekState(), null, compensator);
        long loadIssuedQpc = 5_000;

        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0, loadIssuedQpc);
        compensator.ObserveFrameReady(loadIssuedQpc + Ticks(0.35), generation: 2, sourceSequence: 1);

        compensator.CompensationSeconds.Should().BeApproximately(
            0.3 + (SeekLatencyCompensator.EmaAlpha * (0.35 - 0.3)), 1e-9);
    }

    [Fact]
    public void DiRegistration_SharesOneCompensatorBetweenEngineAndService()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        var compensator = provider.GetRequiredService<SeekLatencyCompensator>();
        ISyncDecisionEngine engine = provider.GetRequiredService<ISyncDecisionEngine>();
        var service = provider.GetRequiredService<TimecodeSyncService>();

        service.LatencyCompensator.Should().BeSameAs(compensator);

        // T9: 製品既定は無効（TCS_SEEK_LATENCY_COMPENSATION=on のときだけ有効）。学習しても行き先は動かない。
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1);
        compensator.CompensationSeconds.Should().Be(0.0);
        engine.Decide(10.0, SeekYieldingState(4.0));
        engine.Decide(10.0, SeekYieldingState(4.0));
        engine.Decide(10.0, SeekYieldingState(4.0)).TargetSeconds.Should().Be(10.0);
    }

    [Fact]
    public void SingleModeCoordinator_PendsCompensatedTargetToSeekState()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(TrackA);
        Learn(compensator, latencySeconds: 0.2, sourceSequence: 1); // L = 0.2
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);
        var service = new TimecodeSyncService(engine, new TimecodeSyncSeekState(), null, compensator);
        var seekTargets = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(service, new SingleModeSyncEffects(
            GetTimePos: () => (0, 4.0),
            BuildPlaybackState: playback => SeekYieldingState(playback),
            SeekTo: target => { seekTargets.Add(target); return true; }));

        coordinator.Apply(10.0).Should().Be(SyncRequestResult.Complete);

        seekTargets.Should().ContainSingle().Which.Should().BeApproximately(10.2, 1e-9);
        service.SeekState.TargetSeconds.Should().BeApproximately(10.2, 1e-9);
    }
}
