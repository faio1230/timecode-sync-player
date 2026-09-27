using FluentAssertions;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 門の統合の前準備: §2 の表で「畳む」「消す」になる門（3・5・7・9・10・11・12・14・17）が
/// 守っている欠陥が、いまのコードで起きないことを決定的なシナリオ層で固定する
/// （docs/design/v0.5.4-gate-unification.md §2・§7）。統合のコミットでこのファイルのテストが
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
        h.Ltc.Duplicate(3.0, TimeSpan.FromMilliseconds(400));    // 250ms を過ぎて損失が確定する保持
        h.Ltc.Jump(10.0);                                        // 保持の後（>250ms）に届いた Jump
        h.Ltc.Duplicate(10.0, TimeSpan.FromMilliseconds(400));   // 着地を観測できる保持
        h.Ltc.Silence(TimeSpan.FromMilliseconds(400));           // 保持フレームが途切れ、理由が信号断へ下がる
        h.Ltc.Jump(12.0);                                        // 保持の後に届いた次の Jump

        RunFor(h, clock, 800);
        sink.Count("signal-loss-confirm").Should().BeGreaterThanOrEqualTo(1, "前提: 保持の損失が確定した");
        Seeks(h).Should().ContainSingle("保持の後に届いた Jump は捨てられず適用される")
            .Which.Value.Should().BeApproximately(10.0, 1e-6);

        RunFor(h, clock, 1_200);
        Seeks(h).Should().HaveCount(2,
            "ラッチを消したので、シーク中・保持中でも次の Jump は捨てられずに適用される（門 3 の削除）");
        Seeks(h)[1].Value.Should().BeApproximately(12.0, 1e-6);
        Report("G3", h, sink);
    }

    [Fact]
    public void G3_MisdecodedJumpBurst_ProducesAtMostOneSeek()
    {
        double[] burstValues = [8.0, 9.5, 11.0, 12.5, 14.0];
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
        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "D38 (b): 遠い要求は到達不能な pending を捨てる（タイムアウトを待たない）");
        sink.Count("pending-timeout").Should().Be(0, "2 秒のタイムアウトを待たない");
        ScenarioMetrics.SeekCount(h).Should().Be(0, "捨てた後も再確認（門 11）とゲート（門 13）を通るまで出さない");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: 再確認中");

        RunFor(h, clock, 800);
        sink.Count("trust-reacquire").Should().Be(1, "門 11: 安定 3 サンプルで判定を再開する");
        Seeks(h).Should().ContainSingle("再開後に 25 秒の要求へ 1 回だけ着地する")
            .Which.AtMilliseconds.Should().BeGreaterThan(requestedAt);
        Seeks(h)[0].Value.Should().BeApproximately(25.0, 0.5);
        Report("G5/G12(far)", h, sink);
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
        int attemptsBefore = h.RateAttempts.Count;   // 追従中の補正はある。この数が増えないことを見る

        // シーク中（保留 + 位置は未信頼）で、ネイティブシークの着地まで位置が凍結した状態。
        h.SyncService.ReportSeekSent(10.0);
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

    // ---- 門 7・11: 着地しないシークは 2 秒で解除し、安定 3 サンプルで再開する ----

    [Fact]
    public void G7_G11_UnreachablePending_TimesOutInTwoSecondsAndResumesAfterThreeStableSamples()
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
        h.Ltc.Duplicate(9.8, TimeSpan.FromMilliseconds(2_200));   // pending の目標の近く（置き換えは起きない）
        h.Ltc.Normal(9.8, TimeSpan.FromMilliseconds(1_600));      // 時間切れの後、再確認を進める有効フレーム

        RunUntil(h, clock, sentAt + 1_800);
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: 2 秒前はまだ保留");
        ScenarioMetrics.SeekCount(h).Should().Be(0, "未信頼と保留の間はシークを出さない");

        long deadline = sentAt + 3_000;
        while (h.SyncService.SeekState.HasPendingSeek && clock.MonotonicMilliseconds < deadline)
            h.AdvanceMilliseconds(40);
        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse("門 7: 2 秒で保留を解除する");
        sink.Count("pending-timeout").Should().Be(1);

        RunFor(h, clock, 700);
        sink.Count("trust-reacquire").Should().Be(1, "門 11: 時間切れの後は安定 3 サンプルで再開する");
        sink.GateEvents.Single(e => e.Name == "trust-reacquire").Message.Should().Contain("samples=3");
        Seeks(h).Should().ContainSingle("再開後は要求へ 1 回だけ着地する");
        Seeks(h)[0].AtMilliseconds.Should().BeGreaterThan(sentAt + 2_000, "タイムアウトの前には出さない");
        Seeks(h)[0].Value.Should().BeInRange(9.8, 11.0);
        Report("G7/G11", h, sink);
    }

    // ---- 門 9: 着地直後の同じところへの再シークは 500ms 抑止する ----

    [Fact]
    public void G9_JustAfterLanding_AReSeekToTheSamePlaceIsSuppressedFor500ms()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(1.0, TimeSpan.FromMilliseconds(120));
        RunFor(h, clock, 120);

        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(800));
        RunUntilSeeks(h, clock, 1);
        h.ManualPause();   // 着地の直後に止めて、位置を着地点（10.0 付近）に保つ

        long settleDeadline = clock.MonotonicMilliseconds + 3_000;
        while (sink.Count("seek-settled") == 0 && clock.MonotonicMilliseconds < settleDeadline)
            h.AdvanceMilliseconds(40);
        sink.Count("seek-settled").Should().Be(1, "前提: 着地した");
        long settledAt = sink.GateEvents.First(e => e.Name == "seek-settled").AtMilliseconds;

        h.Ltc.Normal(11.0, TimeSpan.FromMilliseconds(800));
        RunUntil(h, clock, settledAt + 450);
        Report("G9", h, sink);
        ScenarioMetrics.SeekCount(h).Should().Be(1, "着地から 500ms は近い目標への再シークを出さない（門 9）");
        sink.Count("post-settle-suppress").Should().BeGreaterThan(0, "門 9 の抑止がログに出ている");

        RunUntil(h, clock, settledAt + 900);
        ScenarioMetrics.SeekCount(h).Should().Be(2, "500ms を過ぎたら要求は通る");
        Seeks(h)[1].Value.Should().BeGreaterThan(10.4);
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
        h.BeginManualFileLoad();   // 進捗条件を満たさないロード。解除するまでロード中の場面
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));
        RunFor(h, clock, 400);
        ScenarioMetrics.SeekCount(h).Should().Be(
            0, "ロード中はシークを出さない（門 17/18。この場面では先に TryMarkFileLoaded が止める）");
        h.SyncService.IsLoadingFile.Should().BeTrue("前提: まだロード中");

        // ロードを成立させ、解除と同時に arm されるデバウンス（250ms）の場面へ移る。
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(800));
        h.Playback.SetPosition(1.0);
        h.AdvancePlayback(1.0, renderedFrames: 3);
        RunUntilSeeks(h, clock, 1);
        sink.GateEvents.Should().Contain(e => e.Name == "load-release", "前提: 進捗でロードが解除された");
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
        h.Ltc.Normal(1.5, TimeSpan.FromMilliseconds(200), atMilliseconds: clock.MonotonicMilliseconds);
        RunFor(h, clock, 200);   // 小さな値でゲートを温める
        h.Ltc.Silence(TimeSpan.FromMilliseconds(300));   // 窓を空にし、デバウンスの要らない場面にする
        RunFor(h, clock, 300);

        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));   // 突然の大きな要求
        h.AdvanceMilliseconds(10);   // 1 フレーム目（+ Tick の再評価で 2 観測）
        ScenarioMetrics.SeekCount(h).Should().Be(0, "窓が埋まる前（1 フレーム目）ではシークしない（門 13）");
        h.AdvanceMilliseconds(40);   // 2 フレーム目（+ Tick で 3 観測目）
        h.AdvanceMilliseconds(200);
        Report("G13", h, sink);
        ScenarioMetrics.SyncSeekCount(h).Should().Be(1, "窓（250ms・3 サンプル）が埋まってから 1 回だけシークする");
        Seeks(h).Single().Value.Should().BeInRange(10.0, 10.2);
        HasDebounceMessage(sink).Should().BeFalse("この場面を止めているのは門 13（デバウンスではない）");
    }

    // ---- 門 13（U7）: 確認済みの Jump は 13 を通らずに即シークする ----

    [Fact]
    public void G13_ConfirmedJump_SeeksWithoutWaitingForTheWindow()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        // 追従を続けながら門 14（ロード解除のデバウンス 250ms）も明ける（無音にすると損失扱いになる）。
        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(500));
        RunFor(h, clock, 500);
        ScenarioMetrics.SeekCount(h).Should().Be(0, "前提: 追従中はシークなし（門 13 のゲートは温まっている）");

        // 同じトラック内の Jump は次の 1 フレーム（+1 フレーム）で確認される（門 4）。
        double confirmed = 10.0 + 1.0 / 25.0;
        h.Ltc.Jump(10.0);
        h.Ltc.Normal(confirmed, TimeSpan.FromMilliseconds(80));
        h.AdvanceMilliseconds(10);   // Jump 1 枚目（未確認。シークはまだ）
        ScenarioMetrics.SeekCount(h).Should().Be(0, "確認の前はシークしない（門 4）");

        h.AdvanceMilliseconds(40);   // 確認フレーム。確認済みの Jump の要求が出る
        Report("G13(confirmed jump)", h, sink);
        ScenarioMetrics.SyncSeekCount(h).Should().Be(
            1, "確認済みの Jump は門 13 の窓（250ms・3 サンプル）を待たずに 1 回シークする（U7）");
        Seeks(h).Single().Value.Should().BeApproximately(confirmed, 1e-6);
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
        h.BeginManualFileLoad();   // 再生位置がロード開始と同値で、進捗が満たないためロード中が続く
        h.Ltc.Normal(10.0, TimeSpan.FromMilliseconds(400));
        RunFor(h, clock, 400);

        ScenarioMetrics.SeekCount(h).Should().Be(0, "ロード中は大きな要求でもシークを出さない（門 17/18）");
        h.SyncService.IsLoadingFile.Should().BeTrue("前提: ロード中");
        sink.Count("load-suppress").Should().Be(
            0, "段 0 のとおり、この経路では呼び出し側の TryMarkFileLoaded が先に止める（門 17 の分岐は通らない）");
        Report("G17", h, sink);
    }
}
