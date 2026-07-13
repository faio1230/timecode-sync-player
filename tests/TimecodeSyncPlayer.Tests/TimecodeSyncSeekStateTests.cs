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
}
