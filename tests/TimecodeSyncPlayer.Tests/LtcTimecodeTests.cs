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
    public void ToRealSeconds_DropFrame_DropsTwoFramesAtMinuteBoundary()
    {
        var tc = new LtcTimecode(0, 1, 0, 0, true);

        // 00:01:00;00 は 1800 ではなく 1798 フレーム（毎分 00/01 を飛ばす）→ 1798 ÷ 29.97。
        tc.ToRealSeconds(30).Should().BeApproximately(1798 / (30000.0 / 1001.0), 1e-9);
        tc.ToRealSeconds(30).Should().BeApproximately(59.993267, 1e-6);
    }

    [Fact]
    public void ToString_FormatsCorrectly()
    {
        var tc = new LtcTimecode(1, 2, 3, 4, false);
        tc.ToString().Should().Be("01:02:03:04");
    }

    // ── 29.97 の換算（総フレーム数経由）────────────────────────────
    // totalFrames = ((H×60+M)×60+S)×nominalFps + F（nominalFps は actualFps の四捨五入、
    // 29.97 なら 30）。DropFrame は 2×(総分数 − 総分数/10) を引く。realSeconds =
    // totalFrames ÷ actualFps。24/25/30 は nominal=actual・DF なしなので旧式
    // (H×3600 + M×60 + S + F÷fps) と一致し、回帰しない。

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_UsesTotalFramesAt30000Over1001()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, false);

        double result = tc.ToRealSeconds(30000.0 / 1001.0);

        // 42 フレーム（1 秒 × 30 + 12）÷ 29.97。タイムコードの 1 秒は実時間 1.001 秒。
        result.Should().BeApproximately(42 / (30000.0 / 1001.0), 1e-9);
        result.Should().BeApproximately(1.4014, 1e-6);
    }

    [Fact]
    public void ToRealSeconds_29_97DropFrame_Forces30000Over1001RegardlessOfArgument()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, true);

        // DF でも分をまたがないので飛び番はなく、fps 引数に関わらず actualFps=30000/1001。
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(42 / (30000.0 / 1001.0), 1e-9);
        tc.ToRealSeconds(30.0).Should().BeApproximately(42 / (30000.0 / 1001.0), 1e-9);
    }

    [Fact]
    public void ToRealSeconds_29_97DropFrame_TenMinuteBoundaryKeepsRealTime()
    {
        var tc = new LtcTimecode(0, 10, 0, 0, true);

        // 10 分は 18 フレーム落として 17982 フレーム → ほぼ 600 秒（誤差 0.6ms）。
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(17982 / (30000.0 / 1001.0), 1e-9);
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(599.9994, 1e-6);
    }

    [Fact]
    public void ToRealSeconds_30_UsesPassedFps()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, false);

        tc.ToRealSeconds(30.0).Should().BeApproximately(1.4, 1e-9);
    }

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_OneHourTimecode_ReturnsRealElapsed3603_6Seconds()
    {
        var tc = new LtcTimecode(1, 0, 0, 0, false);

        // 108000 フレーム ÷ 29.97 = 3603.6s。ノミナル 3600 ではない。
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(108000 / (30000.0 / 1001.0), 1e-9);
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(3603.6, 0.001);
    }

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_ThirtyFiveSecondsOfSignal_ReturnsElapsedRealTime()
    {
        // 29.97 NDF を 35 秒流したときのタイムコードはフレーム 1049（0:00:34:29）。
        // 総フレーム換算で実経過時間（35.0016s）と一致する。
        var tc = new LtcTimecode(0, 0, 34, 29, false);

        double result = tc.ToRealSeconds(30000.0 / 1001.0);

        result.Should().BeApproximately(1049 / (30000.0 / 1001.0), 1e-9);
        result.Should().BeApproximately(35.0016, 0.0001);
    }

    [Theory]
    [InlineData(24.0)]
    [InlineData(25.0)]
    [InlineData(30.0)]
    public void ToRealSeconds_IntegerFps_MatchesLegacyFieldFormula(double fps)
    {
        var samples = new[]
        {
            new LtcTimecode(0, 0, 1, 12, false),
            new LtcTimecode(1, 2, 3, 4, false),
            new LtcTimecode(23, 59, 59, (int)fps - 1, false),
        };

        foreach (var tc in samples)
        {
            double legacy = (tc.Hours * 3600.0) + (tc.Minutes * 60.0) + tc.Seconds + (tc.Frames / fps);
            tc.ToRealSeconds(fps).Should().BeApproximately(legacy, 1e-9);
        }
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
