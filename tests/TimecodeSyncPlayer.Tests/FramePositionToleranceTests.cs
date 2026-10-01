using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class FramePositionToleranceTests
{
    private const double Ntsc30 = 30000.0 / 1001.0;

    [Fact]
    public void Fps2997_AcceptsOneFrameAndRejectsTwoFrames()
    {
        // 検証機の R-1・R-5 の偽の失敗（ltc 12.000 / position 12.033、M3 は 29.97）: 位置はメタ表示の fps（29.970）で
        // フレーム番号から秒にする。12:00:00:01 → 12 + 1/29.970。
        double oneFrameLate = 12.0 + 1.0 / 29.970;
        FramePositionTolerance.IsWithinOneFrame(oneFrameLate, 12.0, 29.970).Should().BeTrue();
        FramePositionTolerance.IsWithinOneFrame(12.0 + 1.0 / Ntsc30, 12.0, 29.970).Should().BeTrue();
        FramePositionTolerance.IsWithinOneFrame(12.0 - 1.0 / 29.970, 12.0, 29.970).Should().BeTrue();
        FramePositionTolerance.IsWithinOneFrame(12.0 + 2.0 / 29.970, 12.0, 29.970).Should().BeFalse();
        FramePositionTolerance.IsWithinOneFrame(12.0 + 0.033367, 12.0, 29.970).Should().BeTrue();   // 1 フレーム = 0.033367 s
    }

    [Fact]
    public void Fps2997_IsWiderThanTheOldThirtyFpsTolerance()
    {
        // 以前は丸めた 30fps（1/30 = 0.03333 s）で、29.97 の 1 フレーム（0.03337 s）が落ちていた。
        (Math.Abs((12.0 + 1.0 / 29.970) - 12.0) <= 1.0 / 30.0).Should().BeFalse();
        FramePositionTolerance.OneFrame(29.970).Should().BeGreaterThan(1.0 / 30.0);
        FramePositionTolerance.OneFrame(29.970).Should().BeLessThan(2.0 / 29.970);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(25.0)]
    [InlineData(59.94)]
    [InlineData(24.0)]
    public void OneFrameIsAcceptedAndTwoFramesAreNot(double fps)
    {
        FramePositionTolerance.IsWithinOneFrame(40.0 + 1.0 / fps, 40.0, fps).Should().BeTrue();
        FramePositionTolerance.IsWithinOneFrame(40.0 + 2.0 / fps, 40.0, fps).Should().BeFalse();
    }

    [Fact]
    public void UnknownFps_FallsBackToThirty()
    {
        FramePositionTolerance.FrameSeconds(0.0).Should().BeApproximately(1.0 / 30.0, 1e-12);
        FramePositionTolerance.FrameSeconds(double.NaN).Should().BeApproximately(1.0 / 30.0, 1e-12);
    }

    [Fact]
    public void NonFinitePosition_IsNotWithin()
    {
        FramePositionTolerance.IsWithinOneFrame(double.NaN, 12.0, 29.970).Should().BeFalse();
    }

    [Theory]
    [InlineData("H.264  1920x1080  29.970 fps", 29.970)]
    [InlineData("ProRes 3840x2160 59.940 fps", 59.940)]
    [InlineData("30 fps", 30.0)]
    public void MetaLine_FpsIsParsed(string metaLine, double expected)
    {
        FramePositionTolerance.TryParseMetaLineFps(metaLine, out double fps).Should().BeTrue();
        fps.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("H.264  1920x1080")]
    public void MetaLine_WithoutFps_IsNotParsed(string? metaLine)
    {
        FramePositionTolerance.TryParseMetaLineFps(metaLine, out _).Should().BeFalse();
    }
}
