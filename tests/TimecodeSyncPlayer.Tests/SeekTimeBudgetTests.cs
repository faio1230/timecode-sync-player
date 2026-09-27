using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 K3 (3): 時間切れの逆転を直す。着地の時間切れの定数は 1 つだけで、ギャップのフリーズの
/// 確定（<see cref="GapFreezeHandler.TimeoutSec"/>）と着地の状態
/// （<see cref="TimecodeSyncSeekState.LandingSafetyTimeout"/>）はそれを共有する。shim へ渡す
/// ポンプの予算は着地の時間切れから導き、0.5 秒先に切る（docs/design/v0.5.4-k3-a1.md §8・§11）。
/// </summary>
public class SeekTimeBudgetTests
{
    [Fact]
    public void LandingTimeout_IsTheSingleConstantSharedWithTheGapFreezeAndTheLandingState()
    {
        GapFreezeHandler.TimeoutSec.Should().Be(SeekTimeBudget.LandingTimeoutSeconds,
            "ギャップのフリーズの確定は着地の時間切れと同じ定数から導く（別の 3 秒を持たない）");
        TimecodeSyncSeekState.LandingSafetyTimeout.Should().Be(SeekTimeBudget.LandingTimeout,
            "着地の状態も着地の時間切れと同じ定数から導く");
    }

    [Fact]
    public void PumpBudget_IsShorterThanTheLandingTimeout()
    {
        SeekTimeBudget.PumpBudgetMilliseconds.Should().Be(2500, "着地 3.0 秒 − 0.5 秒");
        (SeekTimeBudget.PumpBudgetMilliseconds / 1000.0).Should()
            .Be(SeekTimeBudget.LandingTimeoutSeconds - SeekTimeBudget.PumpBudgetMarginSeconds);
        SeekTimeBudget.PumpBudgetMilliseconds.Should().BeLessThan(
            (int)(SeekTimeBudget.LandingTimeoutSeconds * 1000),
            "shim のポンプはアプリの着地より先に切れる（時間切れの逆転を戻さない）");
    }

    [Fact]
    public void DefaultLandingTimeout_ExpiresAtTheSharedConstant()
    {
        var state = new TimecodeSyncSeekState();
        DateTime sentAt = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        state.BeginSeek(10.0, sentAt);

        state.ObserveLandingSample(Sample(0.0, 1, 0, 0.0), 0.1,
            sentAt + SeekTimeBudget.LandingTimeout - TimeSpan.FromMilliseconds(1));
        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.WaitingForLanding,
            "着地の時間切れの直前はまだ待つ");

        state.ObserveLandingSample(Sample(0.0, 1, 0, 0.0), 0.1,
            sentAt + SeekTimeBudget.LandingTimeout);
        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.FailedToLand,
            "着地の時間切れで着地せずへ移る（ギャップの確定と同じ定数）");
    }

    private static PlaybackPositionSample Sample(
        double seconds, ulong currentGeneration, ulong deliveredGeneration, double deliveredSeconds) =>
        new(seconds, PlaybackPositionBasis.Pipeline, currentGeneration,
            deliveredSeconds, deliveredGeneration, currentGeneration);
}
