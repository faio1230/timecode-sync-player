using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 段 B: 着地の観測の単体。着地の定義は §9-8（配信世代 >= シーク世代 かつ配信フレームの
/// 位置が target−tol〜target+2×tol）。観測は LTC のフレームの経路と独立に、位置サンプルを
/// 渡すだけで状態が進むことを固定する（§9-7 の 1）。
/// </summary>
public class TimecodeSyncLandingStateTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);

    private static PlaybackPositionSample Sample(
        double seconds, ulong currentGeneration, ulong deliveredGeneration, double deliveredSeconds) =>
        new(seconds, PlaybackPositionBasis.Pipeline, currentGeneration,
            deliveredSeconds, deliveredGeneration, currentGeneration);

    [Fact]
    public void BeginSeek_EntersWaitingForLandingSynchronously()
    {
        var state = new TimecodeSyncSeekState();

        state.BeginSeek(10.0, T0);

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.WaitingForLanding,
            "シークを出すのと同時に（同期的に）着地待ちに入る（§9-7 の 14）");
    }

    [Fact]
    public void ObserveLandingSample_WithDeliveredGenerationAndPosition_LandsWithoutLtcFrames()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        // LTC のフレームは 1 枚も通さない。位置サンプルだけ（保持の Duplicate・UI タイマーと同じ）。
        state.ObserveLandingSample(
            Sample(10.0, currentGeneration: 5, deliveredGeneration: 5, deliveredSeconds: 10.05),
            toleranceSeconds: 0.1, T0.AddMilliseconds(100));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following, "配信の世代と位置で着地する");
        state.LastLanding.Should().NotBeNull();
        state.LastLanding!.Value.DelaySeconds.Should().BeApproximately(0.1, 1e-9);
        state.LastLanding!.Value.DeliveredSeconds.Should().BeApproximately(10.05, 1e-9);
        state.LandingFirstFrameOutsideWindowCount.Should().Be(0);
    }

    [Fact]
    public void ObserveLandingSample_WithoutTheSeekGeneration_StaysWaiting()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        // 現在世代は進んでいる（シーク発行）が、配信は前の世代のまま。
        state.ObserveLandingSample(
            Sample(1.0, currentGeneration: 5, deliveredGeneration: 4, deliveredSeconds: 1.0),
            toleranceSeconds: 0.1, T0.AddMilliseconds(100));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.WaitingForLanding,
            "シークの世代のフレームが届くまでは着地にしない（§9-8）");
    }

    [Fact]
    public void ObserveLandingSample_FirstNewGenerationFrameOutsideTheWindow_IsCountedOnce()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        // (c) 型: 新しい世代の最初のフレームが目標から離れている（前の世代の遅延フレーム等）。
        state.ObserveLandingSample(Sample(13.0, 5, 5, 13.0), 0.1, T0.AddMilliseconds(100));
        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.WaitingForLanding, "窓の外では着地にしない");
        state.LandingFirstFrameOutsideWindowCount.Should().Be(1);

        // 2 枚目以降が外れても数えない。
        state.ObserveLandingSample(Sample(13.0, 5, 5, 13.0), 0.1, T0.AddMilliseconds(200));
        state.LandingFirstFrameOutsideWindowCount.Should().Be(1);

        // 窓に入れば着地する。
        state.ObserveLandingSample(Sample(10.0, 5, 5, 10.02), 0.1, T0.AddMilliseconds(300));
        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following);
        state.LastLanding!.Value.DeliveredSeconds.Should().BeApproximately(10.02, 1e-9);
    }

    [Fact]
    public void ObserveLandingSample_AfterTheSafetyTimeout_FailsAndResumesOnTheNextSample()
    {
        var state = new TimecodeSyncSeekState(TimeSpan.FromSeconds(3));
        state.BeginSeek(10.0, T0);

        state.ObserveLandingSample(Sample(1.0, 5, 4, 1.0), 0.1, T0.AddSeconds(3));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.FailedToLand, "安全の時間切れでは着地せずへ移る");
        state.LastLanding.Should().BeNull();

        state.ObserveLandingSample(Sample(1.0, 5, 4, 1.0), 0.1, T0.AddSeconds(3.1));

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following,
            "着地せずの後は次のサンプルで追従中へ戻る（永久に止めない）");
    }

    [Fact]
    public void ResetLandingState_ReturnsToFollowing()
    {
        var state = new TimecodeSyncSeekState();
        state.BeginSeek(10.0, T0);

        state.ResetLandingState();

        state.LandingPhase.Should().Be(TimecodeSyncLandingPhase.Following);
    }
}
