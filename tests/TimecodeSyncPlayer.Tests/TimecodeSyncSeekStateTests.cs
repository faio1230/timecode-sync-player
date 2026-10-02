using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 段 B2: 着地の状態（A）の単体。門 5・6・7・8・9・10・11・12 を 1 つの状態機械に畳んだ後の
/// 振る舞いを固定する。旧判定（位置の窓・cooldown 200ms・2 秒・500ms・安定 3 サンプル）の単体は
/// 段 B2 で削除した（着地は配信の世代と位置の事象で取る。§9-2・§9-8）。
/// </summary>
public class TimecodeSyncSeekStateTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);

    private static PlaybackPositionSample Sample(
        double seconds, ulong currentGeneration, ulong deliveredGeneration, double deliveredSeconds) =>
        new(seconds, PlaybackPositionBasis.Pipeline, currentGeneration,
            deliveredSeconds, deliveredGeneration, currentGeneration);

    [Fact]
    public void BeginSeek_EntersWaitingAndSuppressesCloseRequests()
    {
        var state = new TimecodeSyncSeekState();

        state.BeginSeek(10.0, T0);

        state.HasPendingSeek.Should().BeTrue();
        state.IsWaitingForLanding.Should().BeTrue();
        state.IsPositionUsable.Should().BeFalse("着地待ちの間は位置を使わない（門 10）");
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Pending);
        state.ShouldSuppressSeek(9.5, toleranceSeconds: 0.2, T0.AddMilliseconds(600),
            requestedTargetSeconds: 10.5).Should().BeTrue("近い要求は着地待ちの間は出さない（門 5・12）");
    }

    [Fact]
    public void ShouldSuppressSeek_FarNewRequest_ReplacesTheWaitWithoutSuppressing()
    {
        // 門 8 / §9-2: 遠い新要求は着地待ちの目標を置き換え、今回のシークを止めない
        // （位置は使わないまま、次に BeginSeek が来たら新しい待ちに入る）。
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(0.0, T0);

        bool suppressed = state.ShouldSuppressSeek(20.0, toleranceSeconds: 0.2, T0.AddMilliseconds(600),
            requestedTargetSeconds: 8.007);

        suppressed.Should().BeFalse();
        state.HasPendingSeek.Should().BeTrue("着地待ちは維持する");
        state.HasPendingReplacement.Should().BeTrue();
        state.TargetSeconds.Should().BeApproximately(8.007, 1e-6);
        state.IsPositionUsable.Should().BeFalse("置き換えの着地まで位置を使わない");
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Superseded);
    }

    [Fact]
    public void ShouldSuppressSeek_WhileFollowing_ReturnsFalse()
    {
        var state = new TimecodeSyncSeekState();

        state.ShouldSuppressSeek(10.0, toleranceSeconds: 0.1, T0).Should().BeFalse();
        state.IsPositionUsable.Should().BeTrue();
    }

    [Fact]
    public void ObserveLandingSample_SettlesAndRestoresPositionUse()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        state.ObserveLandingSample(
            Sample(10.0, currentGeneration: 5, deliveredGeneration: 5, deliveredSeconds: 10.05),
            toleranceSeconds: 0.1, T0.AddMilliseconds(80));

        state.IsWaitingForLanding.Should().BeFalse("配信の世代と位置で着地する（門 6）");
        state.IsPositionUsable.Should().BeTrue();
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Settled);
        state.LastLanding.Should().NotBeNull();
        state.LastLanding!.Value.DelaySeconds.Should().BeApproximately(0.08, 1e-9);
    }

    [Fact]
    public void ObserveLandingSample_AfterTheSafetyTimeout_FailsAndResumesOnTheNextSample()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(3));
        state.BeginSeek(10.0, T0);

        state.ObserveLandingSample(Sample(1.0, 5, 4, 1.0), 0.1, T0.AddSeconds(3));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.FailedToLand);
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.TimedOut);
        state.IsPositionUsable.Should().BeTrue("着地せずでも判定は再開する（永久に止めない）");

        state.ObserveLandingSample(Sample(1.0, 5, 4, 1.0), 0.1, T0.AddSeconds(3.1));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following);
    }

    [Fact]
    public void ResetLandingState_RestoresUseAfterWaiting()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        state.ResetLandingState();

        state.IsPositionUsable.Should().BeTrue();
        state.IsWaitingForLanding.Should().BeFalse();
        state.HasPendingSeek.Should().BeFalse();
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.None);
    }

    [Fact]
    public void ReplaceWaitTarget_FarRequest_ReplacesTheTargetAndKeepsWaiting()
    {
        // D38 (b) / 門 8 / §9-2: 未信頼でも、目標からも現在位置からも離れた要求は着地待ちの目標を
        // 置き換える（位置は使わないまま、置き換えのシークをその場で出して新しい着地待ちに入る）。
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        state.ReplaceWaitTarget(20.0, toleranceSeconds: 0.2, playbackSeconds: 1.0,
            T0.AddMilliseconds(500)).Should().BeTrue();

        state.HasPendingSeek.Should().BeTrue("着地待ちは維持する");
        state.HasPendingReplacement.Should().BeTrue();
        state.TargetSeconds.Should().BeApproximately(20.0, 1e-9);
        state.IsPositionUsable.Should().BeFalse("置き換えのシークが着地するまで位置を使わない");
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Superseded);
    }

    [Fact]
    public void ReplaceWaitTarget_CloseRequest_KeepsTheWait()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        state.ReplaceWaitTarget(10.5, toleranceSeconds: 0.2, playbackSeconds: 1.0, T0).Should().BeFalse();

        state.HasPendingSeek.Should().BeTrue();
        state.HasPendingReplacement.Should().BeFalse();
    }

    // ---- D37-b: シークの着地時間の学習 ----

    [Fact]
    public void SettledSeek_LearnsTheLandingDuration()
    {
        var state = new TimecodeSyncSeekState();
        state.LearnedSeekDurationSeconds.Should().BeNull("未学習の間は保守的な既定値を使う");

        state.BeginSeek(10.0, T0);
        state.ObserveLandingSample(
            Sample(10.0, 5, 5, 10.05), toleranceSeconds: 0.1, T0.AddMilliseconds(500));

        state.LearnedSeekDurationSeconds.Should().BeApproximately(0.5, 1e-9);

        state.ResetLearning();
        state.LearnedSeekDurationSeconds.Should().BeNull();
    }

    // v0.6.3 段 2（設計書 1 節の (a)）: 読み込みの着地（目標なし）の遅れはシークの所要ではないので学習しない
    // （規則 3: c はシークの所要の学習値、学習前は 0）。その後の目標のあるシークの着地は今どおり学習する。
    [Fact]
    public void LoadLanding_IsNotLearned_ButTheNextSeekLandingIs()
    {
        var state = new TimecodeSyncSeekState();

        state.BeginLoadWait(T0);
        state.ObserveLandingSample(Sample(5.0, 5, 5, 5.0), toleranceSeconds: 0.1, T0.AddMilliseconds(200));

        state.HasPendingSeek.Should().BeFalse("前提: 読み込みの世代の最初の配信で着地した");
        state.LastLanding.Should().NotBeNull();
        state.LastLanding!.Value.DelaySeconds.Should().BeApproximately(0.2, 1e-9, "前提: 読み込みの着地の遅れは 0.2 秒");
        state.LearnedSeekDurationSeconds.Should().BeNull("読み込みの着地の遅れは c に学習しない");

        DateTime seekAt = T0.AddSeconds(1);
        state.BeginSeek(10.0, seekAt);
        state.ObserveLandingSample(Sample(10.0, 6, 6, 10.05), toleranceSeconds: 0.1, seekAt.AddMilliseconds(300));

        state.LearnedSeekDurationSeconds.Should().BeApproximately(0.3, 1e-9, "目標のあるシークの着地は学習する");
    }

    [Fact]
    public void SettledSeek_SecondSampleUsesMovingAverage()
    {
        var state = new TimecodeSyncSeekState();

        state.BeginSeek(10.0, T0);
        state.ObserveLandingSample(Sample(10.0, 5, 5, 10.05), 0.1, T0.AddMilliseconds(500));

        DateTime second = T0.AddSeconds(2);
        state.BeginSeek(20.0, second);
        state.ObserveLandingSample(Sample(20.0, 6, 6, 20.05), 0.1, second.AddMilliseconds(1500));

        // 0.5 * 0.7 + 1.5 * 0.3 = 0.8
        state.LearnedSeekDurationSeconds.Should().BeApproximately(0.8, 1e-9);
    }

    // ---- v0.5.4 段 B2 の計測: 着地から 500ms 以内のシーク・速度補正 ----

    [Fact]
    public void NotePostLandingSeekIssued_WithinTheWindow_LogsButDoesNotChangeTheState()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);
        state.ObserveLandingSample(Sample(10.0, 5, 5, 10.05), 0.1, T0.AddMilliseconds(80));

        state.NotePostLandingSeekIssued(10.5, T0.AddMilliseconds(200));

        state.IsPositionUsable.Should().BeTrue("計測は状態を変えない");
        state.LastLanding.Should().NotBeNull();
    }
}
