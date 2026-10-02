using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class ScenarioCapturePoliciesTests
{
    [Theory]
    [InlineData(5.0, true)]    // 目標ちょうど
    [InlineData(5.04, true)]   // 上限（目標 +1 フレーム）
    [InlineData(5.02, true)]   // 目標 +0.5 フレーム
    [InlineData(4.98, false)]  // 目標 −0.5 フレーム（v0.6.4 2-4 (a): 最初のフレームは目標の 0〜+1 フレーム）
    [InlineData(4.96, false)]  // 目標 −1 フレーム（v0.6.3 までの対称の下限）
    [InlineData(5.08, false)]  // 目標 +2 フレーム
    [InlineData(5.05, false)]  // 外（上）
    [InlineData(double.NaN, false)]
    public void ReferenceCaptureReadiness_RequiresThePositionAtTheTarget(double observedPosition, bool expected)
        => ReferenceCaptureReadiness.IsReady(observedPosition, 5.0, 0.04).Should().Be(expected);

    [Fact]
    public void ReferenceCaptureReadiness_ComparesTheLowerBoundOnTheTimeLabelGrid()
    {
        // 位置は TimeLabel（h:mm:ss:ff、ff はフレームの切り捨て）から読むので、目標がその刻みの上に無いと、
        // 目標より後ろのフレームでも読みは目標の手前の刻みになる。末尾の参照（MediaOut − 1/30）を 29.97 で見る例:
        // 目標 19.96667、着地 19.9670（目標の後ろ）→ 表示 0:00:19:28 → 読み 19 + 28/29.97 = 19.93427。
        double frameSeconds = 1.0 / 29.970;
        double target = 20.0 - 1.0 / 30.0;
        double observedLabel = 19.0 + 28.0 / 29.970;
        ReferenceCaptureReadiness.IsReady(observedLabel, target, frameSeconds).Should().BeTrue();
        ReferenceCaptureReadiness.IsReady(19.0 + 27.0 / 29.970, target, frameSeconds)
            .Should().BeFalse("刻みでも 1 つ手前（目標の前のフレーム）は準備まだ");
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.99, true)]
    [InlineData(0.989, false)]
    [InlineData(0.0, false)]
    public void JumpBlackPolicy_ExemptsJumpsIssuedFromABlackPicture(double beforeJumpBlackFraction, bool expected)
        => JumpBlackPolicy.IsExempt(beforeJumpBlackFraction).Should().Be(expected);
}
