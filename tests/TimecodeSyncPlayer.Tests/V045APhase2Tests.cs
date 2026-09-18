using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.5-A フェーズ 2: 評価位置（着地未確認なら配信 PTS + 外挿）を判断にも使う。
/// ここで固定するのは「判断に使う値が評価位置になること」と
/// 「トレース用のクエリ値の意味が変わらないこと」の 2 点。
/// スイッチ（TCS_SYNC_POSITION_FEEDBACK）の既定 off は TimecodeSyncService 側の配線で、
/// off のときは EvalPositionSeconds が判断へ渡らないため、エンジンは従来どおり動く。
/// </summary>
public sealed class V045APhase2Tests
{
    private static SyncDecisionEngine Engine(double seekCostSeconds = 0.0)
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions());
        if (seekCostSeconds > 0) engine.UpdateSeekCostSeconds(seekCostSeconds);
        return engine;
    }

    private static SyncPlaybackState State(double playbackSeconds, double? evalPositionSeconds = null) =>
        new(SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playbackSeconds,
            DurationSeconds: 600.0,
            VideoFps: 60.0,
            TimecodeFps: 25.0)
        {
            EvalPositionSeconds = evalPositionSeconds,
        };

    [Fact]
    public void 評価位置が無ければ従来どおりクエリ値で判断する()
    {
        // クエリ値が LTC と一致していれば、許容内なのでシークは出ない。
        SyncDecision decision = Engine().Decide(10.0, State(playbackSeconds: 10.0));

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void 評価位置があればそちらで判断する_クエリ値が一致していてもずれを検出する()
    {
        // シーク中に観測された形: クエリ値は目標に張り付き（誤差 0 に見える）、
        // 実際の絵は 2 秒前の位置にある。フェーズ 2 は評価位置を見るのでずれを検出する。
        SyncDecision decision = Engine().Decide(
            10.0, State(playbackSeconds: 10.0, evalPositionSeconds: 8.0));

        decision.Action.Should().Be(SyncActionType.Seek,
            "クエリ値では誤差 0 に見えるが、実際の絵は 2 秒遅れている");
    }

    [Fact]
    public void 評価位置があってもクエリ値の意味は変わらない()
    {
        // 判断は評価位置（2 秒のずれ）だが、記録用のクエリ側の差は 0 のまま。
        // これが「既存フィールドの意味を変えない」という契約。
        SyncDecision decision = Engine().Decide(
            10.0, State(playbackSeconds: 10.0, evalPositionSeconds: 8.0));

        decision.QueryDeltaSeconds.Should().BeApproximately(0.0, 1e-9,
            "トレースの delta= はクエリ値基準のまま");
        decision.DeltaSeconds.Should().BeApproximately(2.0, 1e-9,
            "判断に使う差は評価位置基準");
    }

    [Fact]
    public void 着地後は評価位置とクエリ値が一致するので判断も一致する()
    {
        // 着地が確認できた後は評価位置 = クエリ値。両者が同値なら判断も同じになる。
        SyncDecision withEval = Engine().Decide(
            10.0, State(playbackSeconds: 9.0, evalPositionSeconds: 9.0));
        SyncDecision withoutEval = Engine().Decide(10.0, State(playbackSeconds: 9.0));

        withEval.Action.Should().Be(withoutEval.Action);
        withEval.DeltaSeconds.Should().BeApproximately(withoutEval.DeltaSeconds, 1e-9);
    }
}

/// <summary>
/// D37-f: 追従開始とロード成立が同じフレームで起きても、発生元が Other に戻らない。
///
/// 検証機の実測: `windowActive=true origin="Other" learned=none hint=1.990` で
/// `lookahead=0` になり、シーク先が LTC と同値のまま追従開始に 16 秒かかっていた。
/// NotifyLanding(FollowStart) の直後に ReleaseFileLoad が OpenSeekLanding(Other) を
/// 呼んでいたのが原因。
/// </summary>
public sealed class D37fLandingOriginTests
{
    private static TimecodeSyncService Service()
        => new(new SyncDecisionEngine(new SyncDecisionOptions()), new TimecodeSyncSeekState());

    [Fact]
    public void 追従開始の発生元はロード成立で上書きされない()
    {
        TimecodeSyncService svc = Service();
        svc.SetSeekCostHintSeconds(1.99);

        svc.NotifyLanding(LandingOrigin.FollowStart);
        // 同じフレームでロードが成立する（実機で観測された順序）。
        svc.NotifyLanding(LandingOrigin.Other);

        SyncDecision decision = svc.EvaluateDecision(10.0, State(playbackSeconds: 7.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(11.99, 1e-9,
            "LTC 10.0 + ヒント 1.99。Other で上書きされていたら 10.0 になる");
    }

    [Fact]
    public void ギャップ明けの着地は先行しない()
    {
        // 発生元が追従開始でなければ、従来どおり先行させない。
        TimecodeSyncService svc = Service();
        svc.SetSeekCostHintSeconds(1.99);

        svc.NotifyLanding(LandingOrigin.Other);

        SyncDecision decision = svc.EvaluateDecision(10.0, State(playbackSeconds: 7.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(10.0, 1e-9, "ギャップ明けは先行しない");
    }

    [Fact]
    public void 境界ホールドが解除されたら追従開始のエピソードは終わる()
    {
        // D37-g: Single の境界ホールド中は位置がクリップ端に固定され、誤差が許容内に入らない。
        // そのため追従開始の窓が上限まで開いたままになり、ホールド解除後の「巻き戻した LTC への
        // 復帰シーク」にまで先行量が乗っていた（E2E S-3 が 3/3 で落ちた形）。
        TimecodeSyncService svc = Service();
        svc.SetSeekCostHintSeconds(1.99);

        svc.NotifyLanding(LandingOrigin.FollowStart);
        svc.EndFollowStartLanding();

        SyncDecision decision = svc.EvaluateDecision(10.0, State(playbackSeconds: 7.0));

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(10.0, 1e-9,
            "追従開始のエピソードが終わっているので先行しない");
    }

    [Fact]
    public void エピソードを終わらせても次の追従開始は先行できる()
    {
        // 終わらせるのはそのエピソードだけ。状態を潰してはいけない。
        TimecodeSyncService svc = Service();
        svc.SetSeekCostHintSeconds(1.99);

        svc.NotifyLanding(LandingOrigin.FollowStart);
        svc.EndFollowStartLanding();
        svc.NotifyLanding(LandingOrigin.FollowStart);

        SyncDecision decision = svc.EvaluateDecision(10.0, State(playbackSeconds: 7.0));

        decision.TargetSeconds.Should().BeApproximately(11.99, 1e-9,
            "新しい追従開始なので先行量は戻る");
    }

    private static SyncPlaybackState State(double playbackSeconds) =>
        new(SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playbackSeconds,
            DurationSeconds: 600.0,
            VideoFps: 60.0,
            TimecodeFps: 25.0);
}
