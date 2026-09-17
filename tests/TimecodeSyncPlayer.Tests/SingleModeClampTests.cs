using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class SingleModeClampTests
{
    [Theory]
    [InlineData(40.0, 0.0, 20.0, 20.0)]  // MediaOut < 尺: 終端は MediaOut で止まる
    [InlineData(40.0, 0.0, 30.0, 30.0)]  // 尺 = MediaOut: 従来の clamp(0, 尺) と同じ結果
    [InlineData(-2.0, 3.0, 30.0, 3.0)]   // MediaIn より前の LTC は MediaIn
    [InlineData(12.5, 3.0, 25.0, 12.5)]  // 範囲内はそのまま
    public void Target_ClampsToMediaInAndMediaOut(
        double ltcSeconds, double mediaInSeconds, double mediaOutSeconds, double expected) =>
        SingleModeClamp.Target(ltcSeconds, mediaInSeconds, mediaOutSeconds).Should().Be(expected);

    [Theory]
    [InlineData(60.0, 2.0 / 60.0 + 1e-6)]  // 60fps 実素材: 検証機で position=25.033（2 フレーム）
    [InlineData(25.0, 2.0 / 25.0 + 1e-6)]
    [InlineData(30.0, 2.0 / 30.0 + 1e-6)]
    [InlineData(0.0, 2.0 / 30.0 + 1e-6)]   // fps 不明は 30fps として扱う
    public void BoundaryHoldTolerance_UsesTwoFramesOfTheSourceFps(double fps, double expected) =>
        SingleModeClamp.BoundaryHoldTolerance(fps).Should().BeApproximately(expected, 1e-12);
}
