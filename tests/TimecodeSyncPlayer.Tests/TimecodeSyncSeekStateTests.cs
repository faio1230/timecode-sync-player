using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimecodeSyncSeekStateTests
{
    [Fact]
    public void BeginSeek_BlocksAdditionalSeekUntilTargetSettles()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);

        state.ShouldSuppressSeek(9.5, toleranceSeconds: 0.1, now.AddMilliseconds(500))
            .Should().BeTrue();
        state.HasPendingSeek.Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_SettleTick_ReturnsTrueAndClearsHasPendingSeek()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);

        // クールダウン中（500ms → まだ抑止中）
        bool suppressed = state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(500));
        suppressed.Should().BeTrue();
        state.HasPendingSeek.Should().BeTrue();

        // クールダウン経過後のセットルティック → true を返し HasPendingSeek をクリア
        suppressed = state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(800));
        suppressed.Should().BeTrue();          // セットルティックも抑止する
        state.HasPendingSeek.Should().BeFalse();    // ただし状態はクリアされる
    }

    [Fact]
    public void ShouldSuppressSeek_AfterPostSettleSuppress_ReturnsFalse()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);
        // t=500ms: 許容範囲内に到達、_settledAt が記録される（クールダウン開始）
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(500));
        // t=800ms: セットルティック（_settledAt から 300ms 経過 > 200ms クールダウン）
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(800));

        // t=1400ms: PostSettleSuppress(500ms) 経過後（_lastSettledAt=800ms から 600ms）→ false
        bool suppressed = state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(1400));
        suppressed.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppressSeek_DuringPostSettleSuppress_ReturnsFalse_WhenPlaybackHasMovedAwayFromSettledTarget()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(500));
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(800));

        bool suppressed = state.ShouldSuppressSeek(10.35, toleranceSeconds: 0.1, now.AddMilliseconds(1000));

        suppressed.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppressSeek_SettlesPending_WhenPlaybackPassesTargetWithinContinuousPlaybackSlack()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);

        state.ShouldSuppressSeek(10.15, toleranceSeconds: 0.1, now.AddMilliseconds(500))
            .Should().BeTrue();
        bool suppressed = state.ShouldSuppressSeek(10.15, toleranceSeconds: 0.1, now.AddMilliseconds(800));

        suppressed.Should().BeTrue();
        state.HasPendingSeek.Should().BeFalse();
        state.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Settled);
    }

    [Fact]
    public void ShouldSuppressSeek_ClearsPending_WhenTimeoutExpires()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;

        state.BeginSeek(10.0, now);
        bool suppressed = state.ShouldSuppressSeek(9.5, toleranceSeconds: 0.1, now.AddSeconds(3));

        suppressed.Should().BeFalse();
        state.HasPendingSeek.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppressSeek_FarNewRequest_SupersedesUnreachablePending()
    {
        // D20-b (ii): 終端静止中の target 0 pending は playback 20 では永遠に settle しない。
        // 新しい要求（8.007）が pending から離れていれば置き換え、シークを抑止しない。
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;
        state.BeginSeek(0.0, now);

        bool suppressed = state.ShouldSuppressSeek(20.0, toleranceSeconds: 0.2, now.AddMilliseconds(600),
            requestedTargetSeconds: 8.007);

        suppressed.Should().BeFalse();
        state.HasPendingSeek.Should().BeTrue();
        state.TargetSeconds.Should().BeApproximately(8.007, 0.000001);
    }

    [Fact]
    public void ShouldSuppressSeek_CloseNewRequest_KeepsSuppressingPending()
    {
        // 連続して進む LTC の経路: 要求が pending の近くなら従来どおり抑止する。
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = DateTime.UtcNow;
        state.BeginSeek(10.0, now);

        bool suppressed = state.ShouldSuppressSeek(9.5, toleranceSeconds: 0.2, now.AddMilliseconds(600),
            requestedTargetSeconds: 10.5);

        suppressed.Should().BeTrue();
        state.HasPendingSeek.Should().BeTrue();
        state.TargetSeconds.Should().Be(10.0);
    }

    // ---- D37-b: シークの着地時間の学習 ----

    [Fact]
    public void SettledSeek_LearnsTheLandingDuration()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        state.LearnedSeekDurationSeconds.Should().BeNull("未学習の間は保守的な既定値を使う");

        state.BeginSeek(10.0, now);
        // t=500ms: 目標に到達（クールダウン開始）。学習は到達時刻までで数える。
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(500));
        // t=800ms: セトル確定。
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(800));

        state.LearnedSeekDurationSeconds.Should().BeApproximately(0.5, 1e-9);

        state.ResetLearning();
        state.LearnedSeekDurationSeconds.Should().BeNull();
    }

    [Fact]
    public void SettledSeek_SecondSampleUsesMovingAverage()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);

        state.BeginSeek(10.0, now);
        state.ShouldSuppressSeek(10.05, 0.1, now.AddMilliseconds(500));
        state.ShouldSuppressSeek(10.05, 0.1, now.AddMilliseconds(800));

        DateTime second = now.AddSeconds(2);
        state.BeginSeek(20.0, second);
        state.ShouldSuppressSeek(20.05, 0.1, second.AddMilliseconds(1500));
        state.ShouldSuppressSeek(20.05, 0.1, second.AddMilliseconds(1800));

        // 0.5 * 0.7 + 1.5 * 0.3 = 0.8
        state.LearnedSeekDurationSeconds.Should().BeApproximately(0.8, 1e-9);
    }

    // ---- v0.5.4 U4: 位置の信頼（旧 PlaybackPositionTrust から A へ移動） ----

    [Fact]
    public void PositionTrust_StartsUsable()
    {
        var state = new TimecodeSyncSeekState();

        state.IsPositionUsable.Should().BeTrue();
        state.IsWaitingForLanding.Should().BeFalse();
        state.IsReacquiring.Should().BeFalse();
        state.ObservePlaybackPosition(1.0, 1.0).Should().BeTrue();
    }

    [Fact]
    public void PendingSeek_MakesPositionUnusableUntilLanded()
    {
        var state = new TimecodeSyncSeekState();
        DateTime now = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        state.BeginSeek(10.0, now);

        state.IsPositionUsable.Should().BeFalse();
        state.IsWaitingForLanding.Should().BeTrue();
        state.ObservePlaybackPosition(1.0, 1.0).Should().BeFalse("保留中は位置を観測しない");

        // 目標の窓に入り、200ms の冷却を過ぎると着地（その場で位置を使い直す）。
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(500));
        state.ShouldSuppressSeek(10.05, toleranceSeconds: 0.1, now.AddMilliseconds(800));

        state.IsPositionUsable.Should().BeTrue("着地が確認できたらその場で再開する");
        state.IsReacquiring.Should().BeFalse();
    }

    [Fact]
    public void Timeout_RequiresThreeStableSamples()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        state.BeginSeek(10.0, now);
        state.ShouldSuppressSeek(5.0, toleranceSeconds: 0.1, now.AddSeconds(3)).Should().BeFalse("時間切れ");
        state.IsReacquiring.Should().BeTrue();

        double at = 0.0;
        double position = 10.0;
        state.ObservePlaybackPosition(position, at).Should().BeFalse("前のサンプルが無い");
        for (int i = 1; i <= 2; i++)
        {
            at += 0.1;
            position += 0.1;
            state.ObservePlaybackPosition(position, at).Should().BeFalse($"{i} サンプル目");
        }

        at += 0.1;
        position += 0.1;
        state.ObservePlaybackPosition(position, at).Should().BeTrue("3 サンプル続けば再開する");
        state.IsPositionUsable.Should().BeTrue();
    }

    [Fact]
    public void UnstableRate_ResetsTheStableCount()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        state.BeginSeek(10.0, now);
        state.ShouldSuppressSeek(5.0, 0.1, now.AddSeconds(3));

        double at = 0.0;
        state.ObservePlaybackPosition(10.0, at);
        at += 0.1;
        state.ObservePlaybackPosition(10.1, at);
        at += 0.1;
        state.ObservePlaybackPosition(10.2, at);
        state.StableSamples.Should().Be(2);

        // 実時間 0.1 秒に対して 0.5 秒進んだ（レート 5.0）→ 不安定として数え直す。
        at += 0.1;
        state.ObservePlaybackPosition(10.7, at).Should().BeFalse();
        state.StableSamples.Should().Be(0);

        at += 0.1;
        state.ObservePlaybackPosition(10.8, at).Should().BeFalse();
        at += 0.1;
        state.ObservePlaybackPosition(10.9, at).Should().BeFalse();
        at += 0.1;
        state.ObservePlaybackPosition(11.0, at).Should().BeTrue();
    }

    [Fact]
    public void ResetPositionTrust_RestoresUseAfterTimeout()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        DateTime now = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        state.BeginSeek(10.0, now);
        state.ShouldSuppressSeek(5.0, 0.1, now.AddSeconds(3));
        state.IsReacquiring.Should().BeTrue();

        state.ResetPositionTrust();

        state.IsPositionUsable.Should().BeTrue();
        state.IsReacquiring.Should().BeFalse();
    }
}
