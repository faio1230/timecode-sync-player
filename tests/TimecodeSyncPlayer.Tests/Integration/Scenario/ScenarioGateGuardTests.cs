using FluentAssertions;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 門の統合の前準備: §2 の表で「畳む」「消す」になる門（3・5・7・9・10・11・12・14・17）と、
/// 段 B で着地の事象に置き換える門 6（保持だけでも着地を観測する。§9-7-1）が
/// 守っている欠陥が、いまのコードで起きないことを決定的なシナリオ層で固定する
/// （docs/design/v0.5.4-gate-unification.md §2・§7・§9-7）。
/// 統合のコミットでこのファイルのテストが
/// 緑のままであることが、門を畳んでも欠陥が戻らない証拠になる。
/// 門 20 は既存の LtcSingleClipEndHoldTests.BoundaryHold_DoesNotIssueExplicitLandingToTheEdge が押さえる。
/// </summary>
[Collection("Serilog global logger")]
public sealed class ScenarioGateGuardTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private const int BaseMilliseconds = 60_000;
    private readonly ITestOutputHelper _output;

    public ScenarioGateGuardTests(ITestOutputHelper output) => _output = output;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange(
        SyncMode mode = SyncMode.Continue, LtcSignalLossMode lossMode = LtcSignalLossMode.RunThrough)
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
        };
        if (mode != SyncMode.Continue)
            h.ChangeMode(mode);
        return (h, clock);
    }

    /// <summary>40ms の LTC グリッドで、指定の仮想時刻まで進める。</summary>
    private static void RunUntil(SyncScenarioHarness h, ScenarioClock clock, long targetMilliseconds)
    {
        int guard = 0;
        while (clock.MonotonicMilliseconds < targetMilliseconds && guard++ < 100_000)
            h.AdvanceMilliseconds(40);
    }

    private static void RunFor(SyncScenarioHarness h, ScenarioClock clock, int milliseconds) =>
        RunUntil(h, clock, clock.MonotonicMilliseconds + milliseconds);

    private static void RunUntilSeeks(SyncScenarioHarness h, ScenarioClock clock, int expectedSeeks)
    {
        long deadline = clock.MonotonicMilliseconds + 5_000;
        while (ScenarioMetrics.SeekCount(h) < expectedSeeks && clock.MonotonicMilliseconds < deadline)
            h.AdvanceMilliseconds(40);
    }

    private static IReadOnlyList<ScenarioEvent> Seeks(SyncScenarioHarness h) =>
        h.Events.Where(e => e.Kind == "seek").ToArray();

    private static bool HasDebounceMessage(ScenarioLogSink sink) =>
        sink.GateEvents.Any(e => e.Message.Contains("Debounced", StringComparison.Ordinal));

    private void Report(string name, SyncScenarioHarness h, ScenarioLogSink sink)
    {
        _output.WriteLine(
            $"{name}: seeks={ScenarioMetrics.SeekCount(h)} sync={ScenarioMetrics.SyncSeekCount(h)} " +
            $"landing={ScenarioMetrics.LandingSeekCount(h)} timeouts={sink.Count("pending-timeout")} " +
            $"gates={string.Join(",", sink.GateEvents
                .GroupBy(e => e.Name)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}"))}");
        foreach (ScenarioEvent seek in Seeks(h))
            _output.WriteLine($"  seek@{seek.AtMilliseconds - BaseMilliseconds}ms {seek.Value:F3}");
        foreach (ScenarioGateEvent gate in sink.GateEvents.Where(e => e.Name is "load-release" or "seek-settled")
                     .Take(6))
            _output.WriteLine($"  gate@{gate.AtMilliseconds - BaseMilliseconds}ms {gate.Name} {gate.Message}");
    }

    // ---- 門 3: 保持の後（250ms 超）に届いた Jump が捨てられずに適用される（U1 で門を消した） ----

    [Fact]
    public void G3_JumpAfterHeldLossTimeout_IsAppliedAndNotDropped()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode: LtcSignalLossMode.RunThrough);
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
        // v0.6.1 段 A: 保持は直前の Normal の終わりの値（3.16）の繰り返し（3.0 だと 4 フレーム戻る Jump になる）。
        h.Ltc.Duplicate(3.16, TimeSpan.FromMilliseconds(400));   // 250ms を過ぎて損失が確定する保持
        h.Ltc.Jump(10.0);                                        // 保持の後（>250ms）に届いた Jump
        h.Ltc.Duplicate(10.0, TimeSpan.FromMilliseconds(400));   // 着地を観測できる保持
        h.Ltc.Silence(TimeSpan.FromMilliseconds(400));           // 保持フレームが途切れ、理由が信号断へ下がる
        // v0.5.4 B7: Jump はすべて次の 1 フレームの値の連続性で確かめる。LTC が進み続ける（+1 フレーム）形にする
        // （1 枚だけで途切れる Jump は壊れたフレームと区別できないので適用しない。D30）。
        h.Ltc.Jump(12.0).Normal(12.04, TimeSpan.FromMilliseconds(40));   // 保持の後に届いた次の Jump

        RunFor(h, clock, 800);
        sink.Count("signal-loss-confirm").Should().BeGreaterThanOrEqualTo(1, "前提: 保持の損失が確定した");
        Seeks(h).Should().ContainSingle("保持の後に届いた Jump は捨てられず適用される")
            .Which.Value.Should().BeApproximately(10.0, 1e-6);

        RunFor(h, clock, 1_200);
        Seeks(h).Should().HaveCount(2,
            "ラッチを消したので、シーク中・保持中でも次の Jump は捨てられずに適用される（門 3 の削除）");
        Seeks(h)[1].Value.Should().BeApproximately(12.04, 1e-6, "確認したフレーム（12.04）の値で適用する（v0.5.4 B7）");
        Report("G3", h, sink);
    }

    [Fact]
    public void G3_MisdecodedJumpBurst_ProducesAtMostOneSeek()
    {
        // v0.6.1 段 A: 誤値もフレームの格子の上の値にする（LTC はフレーム単位。9.5・12.5 は 25fps の格子に無く、
        // 実の口でフレームへ丸められて確認の +1 フレームと食い違っていた）。
        double[] burstValues = [8.0, 9.52, 11.0, 12.52, 14.0];
        for (int burstLength = 2; burstLength <= 5; burstLength++)
        {
            (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
            using var sink = new ScenarioLogSink(clock);
            h.AddTrack("A", 0, 30);
            h.ManualPlay();
            h.AdvancePlayback(3.0);

            h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
            RunFor(h, clock, 200);
            ScenarioMetrics.SeekCount(h).Should().Be(0, $"前提: 追従中はシークなし（Jump {burstLength} 枚）");

            // 同じトラック内の値のばらついた誤デコード Jump が続けて届く（2〜5 枚）。
            for (int i = 0; i < burstLength; i++)
                h.Ltc.Jump(burstValues[i]);
            double confirmed = burstValues[burstLength - 1] + 1.0 / 25.0;
            h.Ltc.Normal(confirmed, TimeSpan.FromMilliseconds(40));   // 最後の Jump を確認する 1 フレーム
            RunFor(h, clock, 400);

            Seeks(h).Should().ContainSingle(
                    $"同じトラック内の Jump は次の 1 フレームで確認するまで適用しない（門 4）。" +
                    $"値のばらついた {burstLength} 枚でも着地は 1 本")
                .Which.Value.Should().BeApproximately(confirmed, 1e-6);
            Report($"G3(burst {burstLength})", h, sink);
        }
    }

    // ---- 門 5・10・12: 着地を待つ間は位置を信頼せず、新しいシークを出さない ----

    [Fact]
    public void G5_G10_G12_WhileWaitingForLanding_CloseNewRequestsDoNotIssueASecondSeek()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);

        // 着地しないシークを発行した状態（保留 + 位置は未信頼）。
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: 保留がある");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: 位置が未信頼");

        h.Ltc.Normal(1.4, TimeSpan.FromMilliseconds(600));   // 保留の目標からも現在位置からも近い要求
        RunFor(h, clock, 600);

        ScenarioMetrics.SeekCount(h).Should().Be(
            0, "着地を待つ間に新しいシークを出さない（門 5。未信頼の間は判定しない門 10・12 も効く）");
        sink.Count("pending-suppress").Should().BeGreaterThan(0, "門 5: 保留の抑止がログに出ている");
        sink.Count("untrusted-defer").Should().BeGreaterThan(0, "門 12: 未信頼の間の判定停止がログに出ている");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse(
            "門 10: シーク発行から着地までは位置を判定に使わない");
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: 2 秒のタイムアウトには達していない");
        Report("G5/G10/G12", h, sink);
    }

    [Fact]
    public void G5_G12_FarRequestWhileUntrusted_DiscardsPendingWithoutWaitingForTheTimeout()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: 位置が未信頼");

        // デバウンス（250ms）を明けるためだけに少し待つ。保留のタイムアウト（2 秒）には遠い。
        h.Ltc.Normal(1.6, TimeSpan.FromMilliseconds(300));
        RunFor(h, clock, 300);
        long requestedAt = clock.MonotonicMilliseconds;
        h.Ltc.Normal(25.0, TimeSpan.FromMilliseconds(400));   // pending からも現在位置からも 4×tol 超

        RunFor(h, clock, 80);
        // v0.5.4 段 B / 門 8 / §9-2: 前の着地待ちを捨てずに目標を置き換え、その場でシークを出す。
        sink.Count("pending-replace").Should().BeGreaterThanOrEqualTo(1, "門 8: 遠い要求は目標を置き換える");
        sink.Count("pending-timeout").Should().Be(0, "2 秒のタイムアウトを待たない");
        Seeks(h).Should().ContainSingle("置き換えた目標へその場で 1 回着地する")
            .Which.AtMilliseconds.Should().BeGreaterThan(requestedAt);
        Seeks(h)[0].Value.Should().BeApproximately(25.0, 0.5);
        h.SyncService.SeekState.LastLanding!.Value.TargetSeconds.Should().BeApproximately(25.0, 0.5,
            "置き換えた目標へ着地した（配信の事象。ハーネスは即時配信）");

        RunFor(h, clock, 300);
        sink.Count("trust-reacquire").Should().Be(0, "門 11 の再確認は畳んだ（着地の事象で取る）");
        Report("G5/G12(far)", h, sink);
    }

    // ---- 門 6 + 段 B §9-7-1: 着地の観測は LTC のフレームの経路に依存しない ----

    [Fact]
    public void G6_LandingWhileLtcIsHeld_IsObservedWithoutNormalFrames()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode: LtcSignalLossMode.RunThrough);
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);

        // 着地しないシークを発行し、偽の再生 API の世代の最初のフレームを目標へ配信させる。
        h.SyncService.ReportSeekSent(10.0);
        h.Playback.SeekLandingDelaySeconds = 0.05;
        h.Playback.Seek(10.0);

        // LTC は保持（Duplicate）だけ。Normal（有効フレーム）の経路を使わずに着地を観測できるか。
        // 着地の観測は 1 フレームで届くが、今の門 6 は SettleCooldown（200ms）を置くので、
        // 確定（seek-settled）は観測の 200ms 後になる。段 B ではここが着地の事象 1 つになる。
        h.Ltc.Duplicate(10.0, TimeSpan.FromMilliseconds(600));
        RunFor(h, clock, 400);

        sink.Count("seek-settled").Should().Be(1, "着地は観測される");
        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "D38 (a) / 段 B §9-7-1: 保持の Duplicate だけでも着地を観測して確定する（現行は cooldown 200ms 込み）");
        h.SyncService.IsPlaybackPositionUsable.Should().BeTrue("着地で位置の信頼が戻る");
        Seeks(h).Should().BeEmpty("着地の観測はシークを出さない");

        // 段 B1: 新しい判定（着地の状態）も、LTC のフレームの経路と独立に観測される（§9-7 の 1）。
        h.SyncService.SeekState.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following);
        h.SyncService.SeekState.LastLanding.Should().NotBeNull("配信の世代と位置で着地した");
        h.SyncService.SeekState.LastLanding!.Value.DelaySeconds.Should().BeLessThan(0.2,
            "着地は配信の最初のフレームで確定する（旧 門 6 の 200ms の cooldown を含まない）");
        h.SyncService.SeekState.LandingFirstFrameOutsideWindowCount.Should().Be(0);
        // v0.5.4 段 B3: 読み込みの着地（target=NaN）も new-landing を出すので、シークの着地だけを数える。
        sink.GateEvents.Count(e => e.Name == "new-landing" && !e.Message.Contains("target=NaN"))
            .Should().Be(1, "シークの着地がログに出る");
        Report("G6(held landing)", h, sink);
    }

    [Fact]
    public void G6_FirstNewGenerationFrameOutsideTheWindow_IsCounted()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode: LtcSignalLossMode.RunThrough);
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);

        // 着地の遅れの後、目標から離れた位置のフレームが新しい世代として配信される型（§9-8 の (c)）。
        h.SyncService.ReportSeekSent(10.0);
        h.Playback.SeekOvershootSeconds = 3.0;
        h.Playback.SeekLandingDelaySeconds = 0.05;
        h.Playback.Seek(10.0);
        h.Ltc.Duplicate(10.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 80);

        sink.Count("landing-first-frame-outside-window").Should().Be(1,
            "新しい世代の最初のフレームが着地の窓の外（shim の通知が要るかの材料）");
        h.SyncService.SeekState.LandingFirstFrameOutsideWindowCount.Should().Be(1);
        h.SyncService.SeekState.LandingPhase.Should().Be(TimecodeSyncLandingPhase.WaitingForLanding,
            "窓の外のフレームでは着地にしない");
        Report("G6(outside window)", h, sink);
    }

    // ---- 門 10 の備考（TSP-Fable のレビュー）: シーク中に rate.instant を出さない ----

    [Fact]
    public void G10_WhileSeekIsPending_NoRateInstantIsEmitted()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(mode: SyncMode.Single);
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);
        // シーク中（保留 + 位置は未信頼）で、ネイティブシークの着地まで位置が凍結した状態。
        h.SyncService.ReportSeekSent(10.0);
        // v0.5.4 B6b（追補 5）: relocate の発行の時点で倍率を 1.0 に戻す（規則 3: varispeed を持ち越さない）。
        // その 1 回は発行の時点のもので、この後の着地待ちの間に増えないことを見る。
        h.AppliedRates[^1].Should().Be(1.0, "relocate の発行で 1.0 に戻す");
        int attemptsBefore = h.RateAttempts.Count;   // 追従中の補正と発行時の戻し。この数が増えないことを見る
        h.Playback.SeekLandingDelaySeconds = 1.0;
        h.Playback.Seek(10.0);
        h.Playback.IsSeeking().Should().BeTrue("前提: 着地まで位置が凍結している");
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: 保留がある");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: 位置が未信頼");

        h.Ltc.Normal(2.0, TimeSpan.FromMilliseconds(400));   // 凍結した位置とかけ離れた LTC
        RunFor(h, clock, 400);

        h.RateAttempts.Count.Should().Be(
            attemptsBefore, "シーク中（A が pending を持つ間）は Smooth の補正を評価しない（rate.instant を出さない）");
        h.AppliedRates.Count.Should().Be(attemptsBefore, "シーク中はレートを適用しない");
        Report("G10(rate)", h, sink);
    }

    // ---- 門 7（畳み後）: 着地しないシークは安全の時間切れ（3 秒）で解除し、次のサンプルで再開する ----

    [Fact]
    public void G7_G11_UnreachablePending_TimesOutInThreeSecondsAndResumesWithoutReacquire()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);

        // 目標 10.0 に着地しないシーク（位置は 1:1 で進み、目標の窓に入らない）。
        h.SyncService.ReportSeekSent(10.0);
        long sentAt = clock.MonotonicMilliseconds;
        h.Ltc.Duplicate(9.8, TimeSpan.FromMilliseconds(2_200));   // 着地待ちの目標の近く（置き換えは起きない）
        h.Ltc.Normal(9.8, TimeSpan.FromMilliseconds(1_600));      // 時間切れの後、判定を進める有効フレーム

        RunUntil(h, clock, sentAt + 2_000);
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue(
            "前提: 3 秒前はまだ着地待ち（旧の 2 秒では解けない。D3 の張り付いた保留の型）");
        ScenarioMetrics.SeekCount(h).Should().Be(0, "着地待ちの間はシークを出さない");

        long deadline = sentAt + 4_000;
        while (h.SyncService.SeekState.HasPendingSeek && clock.MonotonicMilliseconds < deadline)
            h.AdvanceMilliseconds(40);
        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "安全の時間切れ（3 秒）で着地待ちを解く（D3 の型が解けることの主張）");
        sink.Count("landing-safety-timeout").Should().Be(1);
        sink.Count("pending-timeout").Should().Be(1, "旧 G7 の計測名でも 1 回");

        RunFor(h, clock, 700);
        sink.Count("trust-reacquire").Should().Be(0, "門 11 の再確認（安定 3 サンプル）は畳んだ");
        Seeks(h).Should().ContainSingle("再開後は要求へ 1 回だけ着地する");
        Seeks(h)[0].AtMilliseconds.Should().BeGreaterThan(sentAt + 3_000, "時間切れの前には出さない");
        Seeks(h)[0].Value.Should().BeInRange(9.8, 11.0);
        Report("G7/G11", h, sink);
    }

    // ---- 門 9: 着地直後の同じところへの再シークは 500ms 抑止する ----

    [Fact]
    public void G9_JustAfterLanding_AReSeekToTheSamePlaceIsNotIssued()
    {
        // D7-a: 着地直後の同じ場所への再シーク。旧は門 9 の 500ms で隠していた。畳んだ後は
        // 「着地した位置が目標の近くなら、着地の直後に同じ場所へ再シークしない」を主張する。
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(120));
        RunFor(h, clock, 120);

        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(800));
        RunUntilSeeks(h, clock, 1);
        // 着地を観測してから、同じ場所（目標 10.0 の近く）の LTC を続ける。
        RunFor(h, clock, 300);
        h.SyncService.SeekState.LastLanding.Should().NotBeNull("前提: 着地した（配信の事象）");
        ScenarioMetrics.SeekCount(h).Should().Be(1, "着地の直後に同じ場所へ再シークしない（D7-a）");
        sink.Count("post-landing-seek").Should().Be(0,
            "着地から 500ms 以内の同期シークは 0（D7-a の計測。v0.5.4 段 B3 で読み込みの着地は数えない）");

        // 別の場所へ動けば要求は通る（抑止ではない）。速度補正の範囲（1 秒）を超える差にする。
        h.Ltc.Normal(12.0, TimeSpan.FromMilliseconds(800));
        RunFor(h, clock, 800);
        ScenarioMetrics.SeekCount(h).Should().Be(2, "別の場所への要求は通る");
        Seeks(h)[1].Value.Should().BeGreaterThan(11.4);
        Report("G9", h, sink);
    }

    // ---- 門 14: シークのデバウンス（消す候補。13 だけで足りるかを測るための場面） ----

    [Fact]
    public void G14_AfterAFileLoad_RequestsWithin250msAreCollapsedIntoOneSeek()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0, renderedFrames: 5);
        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);

        h.Playback.SetPosition(0.0);
        h.ManualPause();
        // v0.5.4 段 B3: 読み込みの世代は進んでいるが、最初のフレームがまだ配信されていない場面。
        h.BeginManualFileLoadWithoutLanding();
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));
        RunFor(h, clock, 400);
        ScenarioMetrics.SeekCount(h).Should().Be(
            0, "読み込みの着地（最初のフレームの配信）までシークを出さない（門 17/18）");
        h.SyncService.IsLoadingFile.Should().BeTrue("前提: まだ着地していない");

        // ロードを成立させ、解除と同時に arm されるデバウンス（250ms）の場面へ移る。
        h.Playback.DeliverLoadLanding();
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(800));
        h.Playback.SetPosition(1.0);
        h.AdvancePlayback(1.0, renderedFrames: 3);
        RunUntilSeeks(h, clock, 1);
        sink.GateEvents.Should().Contain(e => e.Name == "load-release", "前提: 着地でロードが解除された");
        long firstSeekAt = Seeks(h)[0].AtMilliseconds;
        long releasedAt = sink.GateEvents.Last(e => e.Name == "load-release" && e.AtMilliseconds <= firstSeekAt)
            .AtMilliseconds;
        Report("G14", h, sink);
        HasDebounceMessage(sink).Should().BeTrue("門 14: 解除直後の要求はデバウンスで止まる");
        (firstSeekAt - releasedAt).Should().BeGreaterThanOrEqualTo(240,
            "解除から 250ms 以内にシークを出さない（デバウンスが要求を 1 本に畳む）");
        (firstSeekAt - releasedAt).Should().BeLessThanOrEqualTo(440, "250ms を過ぎたら通る");
        Seeks(h).Should().ContainSingle("要求が連続しても 250ms 以内に 2 本は出ない");
    }

    // ---- 門 13: 瞬間値では出さず、0.25 秒窓の 3 サンプルを要する ----

    [Fact]
    public void G13_ASuddenLargeRequest_IsNotSeekedUntilThreeSamples()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 600);   // 初回の自動ロード（SwitchTrack）と解除直後のデバウンスを明ける
        // v0.6.1 段 B: 値は 25fps の格子に乗せる（1.5 は 37.5 フレームで、丸めで同値・2 フレーム飛びが混ざる）。
        h.Ltc.Normal(1.52, TimeSpan.FromMilliseconds(200), atMilliseconds: clock.MonotonicMilliseconds);
        RunFor(h, clock, 200);   // 小さな値でゲートを温める
        h.Ltc.Silence(TimeSpan.FromMilliseconds(300));   // 窓を空にし、デバウンスの要らない場面にする
        RunFor(h, clock, 300);

        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));   // 突然の大きな要求
        h.AdvanceMilliseconds(10);   // 1 フレーム目（+ Tick の再評価で 2 観測）
        ScenarioMetrics.SeekCount(h).Should().Be(0, "窓が埋まる前（1 フレーム目）ではシークしない（門 13）");
        h.AdvanceMilliseconds(40);   // 2 フレーム目（+ Tick で 3 観測目）
        // v0.6.1 段 B: 40ms ごとに進める（1 回で 200ms 進めると、その間のフレームがまとめて届き、フレーム終端からの
        // 経過（サンプル時計の age）が付いて目標が先へずれる）。
        for (int i = 0; i < 5; i++)
            h.AdvanceMilliseconds(40);
        Report("G13", h, sink);
        ScenarioMetrics.SyncSeekCount(h).Should().Be(1, "窓（250ms・3 サンプル）が埋まってから 1 回だけシークする");
        Seeks(h).Single().Value.Should().BeInRange(10.0, 10.2);
        HasDebounceMessage(sink).Should().BeFalse("この場面を止めているのは門 13（デバウンスではない）");
    }

    // ---- 門 17: ロード中はシークを出さない ----

    [Fact]
    public void G17_DuringAFileLoad_NoSeekIsIssuedUntilTheLoadIsReleased()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);
        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(200));
        RunFor(h, clock, 200);
        ScenarioMetrics.SeekCount(h).Should().Be(0, "前提: ここまではシークなし");

        h.Playback.SetPosition(0.0);
        h.ManualPause();
        // v0.5.4 段 B3: 読み込みの世代は進んでいるが、最初のフレームがまだ配信されていない場面。
        h.BeginManualFileLoadWithoutLanding();
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));
        RunFor(h, clock, 400);

        ScenarioMetrics.SeekCount(h).Should().Be(0, "読み込みの着地まで大きな要求でもシークを出さない（門 17/18）");
        h.SyncService.IsLoadingFile.Should().BeTrue("前提: まだ着地していない");
        sink.Count("load-suppress").Should().Be(
            0, "段 0 のとおり、この経路では着地待ち（未信頼の決定）が止める（門 17 の分岐は通らない）");

        // 着地すれば解除され、判定が再開して要求が通る。
        h.Playback.DeliverLoadLanding();
        RunUntilSeeks(h, clock, 1);
        h.SyncService.IsLoadingFile.Should().BeFalse("着地でロードが解除された");
        Report("G17", h, sink);
    }
}
