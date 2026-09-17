using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class ScenarioCapturePoliciesTests
{
    [Theory]
    [InlineData(false, false, true, true)]   // 前の採取と違う絵 + 目標位置 → 採取可
    [InlineData(false, true, true, false)]   // 前と同じ絵（D25 の古いフレーム）→ まだ待つ
    [InlineData(false, false, false, false)] // 位置が目標 ±1 フレームの外 → まだ待つ
    [InlineData(true, false, true, true)]    // 前の採取が無い（最初の head）は位置が合えば可
    public void ReferenceCaptureReadiness_RequiresAChangedPictureAtTheSeekTarget(
        bool previousIsNull, bool sameAsPrevious, bool atTarget, bool expected)
    {
        FrameSignature candidate = Signature(10);
        FrameSignature? previous = previousIsNull ? null : sameAsPrevious ? candidate : Signature(200);
        double observedPosition = atTarget ? 5.0 : 9.0;

        ReferenceCaptureReadiness.IsReady(candidate, previous, observedPosition, 5.0, 0.04)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.99, true)]
    [InlineData(0.989, false)]
    [InlineData(0.0, false)]
    public void JumpBlackPolicy_ExemptsJumpsIssuedFromABlackPicture(double beforeJumpBlackFraction, bool expected)
        => JumpBlackPolicy.IsExempt(beforeJumpBlackFraction).Should().Be(expected);

    private static FrameSignature Signature(byte level) =>
        new(level, level, level, 0.0, 4, [level, level, level, level, level, level, level, level, level, level, level, level]);
}
