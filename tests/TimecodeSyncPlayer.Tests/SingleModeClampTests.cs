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
}
