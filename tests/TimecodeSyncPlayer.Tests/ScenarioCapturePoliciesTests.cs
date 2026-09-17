using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class ScenarioCapturePoliciesTests
{
    [Theory]
    [InlineData(5.0, true)]    // 目標ちょうど
    [InlineData(5.04, true)]   // 上限（目標 ±1 フレーム）
    [InlineData(4.96, true)]   // 下限
    [InlineData(4.95, false)]  // 外（下）
    [InlineData(5.05, false)]  // 外（上）
    [InlineData(double.NaN, false)]
    public void ReferenceCaptureReadiness_RequiresThePositionAtTheTarget(double observedPosition, bool expected)
        => ReferenceCaptureReadiness.IsReady(observedPosition, 5.0, 0.04).Should().Be(expected);

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.99, true)]
    [InlineData(0.989, false)]
    [InlineData(0.0, false)]
    public void JumpBlackPolicy_ExemptsJumpsIssuedFromABlackPicture(double beforeJumpBlackFraction, bool expected)
        => JumpBlackPolicy.IsExempt(beforeJumpBlackFraction).Should().Be(expected);
}
