using FluentAssertions;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

public class LtcTimecodeTests
{
    [Fact]
    public void ToRealSeconds_NonDropFrame_ReturnsCorrectValue()
    {
        var tc = new LtcTimecode(1, 2, 3, 4, false);
        var result = tc.ToRealSeconds(30);
        result.Should().BeApproximately(1 * 3600.0 + 2 * 60.0 + 3 + 4.0 / 30.0, 0.001);
    }

    [Fact]
    public void ToRealSeconds_DropFrame_Uses30000Over1001()
    {
        var tc = new LtcTimecode(0, 0, 0, 0, true);
        var result = tc.ToRealSeconds(30);
        result.Should().Be(0);
    }

    [Fact]
    public void ToRealSeconds_DropFrame_CorrectsFps()
    {
        var tc = new LtcTimecode(0, 1, 0, 0, true);
        var result = tc.ToRealSeconds(30);
        result.Should().BeApproximately(60.0, 0.001);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var tc = new LtcTimecode(1, 2, 3, 4, false);
        tc.ToString().Should().Be("01:02:03:04");
    }

    [Fact]
    public void ToString_DropFrame_UsesSemicolon()
    {
        var tc = new LtcTimecode(1, 2, 3, 4, true);
        tc.ToString().Should().Be("01:02:03;04");
    }

    [Fact]
    public void FormatRealTime_PositiveValue_FormatsWithThreeDecimals()
    {
        var result = LtcTimecode.FormatRealTime(10376.48);
        result.Should().Be("10376.480 s");
    }

    [Fact]
    public void FormatRealTime_NegativeValue_ClampsToZero()
    {
        var result = LtcTimecode.FormatRealTime(-1.0);
        result.Should().Be("0.000 s");
    }
}
