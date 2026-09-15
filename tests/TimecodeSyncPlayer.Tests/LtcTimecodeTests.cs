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

    // ── 29.97 の換算（現行挙動の固定）─────────────────────────────
    // ToRealSeconds は H×3600 + M×60 + S + F÷fps、DropFrame のとき fps は 30000/1001。
    // 29.97 NDF はフィールド表現が 30fps と同じ（飛び番なし）で、実時間はフレーム総数
    // からしか復元できない。ここでは現行式が返す値を固定し、実時間との差も記録する。
    // 修正・仕様変更はこのテストでは行わない（扱いは別途確認中）。

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_MapsFramesAt30000Over1001()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, false);

        double result = tc.ToRealSeconds(30000.0 / 1001.0);

        result.Should().BeApproximately(1 + (12 / (30000.0 / 1001.0)), 1e-9);
        result.Should().BeApproximately(1.4004, 1e-6);
    }

    [Fact]
    public void ToRealSeconds_29_97DropFrame_Forces30000Over1001RegardlessOfArgument()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, true);

        // DF フラグは fps の選択にのみ効き、飛び番（毎分 00/01 の欠番）はこの式には入らない。
        tc.ToRealSeconds(30000.0 / 1001.0).Should().BeApproximately(1.4004, 1e-6);
        tc.ToRealSeconds(30.0).Should().BeApproximately(1.4004, 1e-6);
    }

    [Fact]
    public void ToRealSeconds_30_UsesPassedFps()
    {
        var tc = new LtcTimecode(0, 0, 1, 12, false);

        tc.ToRealSeconds(30.0).Should().BeApproximately(1.4, 1e-9);
    }

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_OneHourTimecode_ReturnsNominal3600Seconds()
    {
        var tc = new LtcTimecode(1, 0, 0, 0, false);

        // 現行挙動: ノミナル秒の 3600 を返す。29.97 NDF でこのタイムコードに達する実時間は
        // 108000 フレーム ÷ (30000/1001) = 3603.6s で、現行値との差は 3.6s。
        tc.ToRealSeconds(30000.0 / 1001.0).Should().Be(3600.0);
        (108000 / (30000.0 / 1001.0)).Should().BeApproximately(3603.6, 0.001);
    }

    [Fact]
    public void ToRealSeconds_29_97NonDropFrame_ThirtyFiveSecondsOfSignal_ReturnsAbout34_968Seconds()
    {
        // 29.97 NDF を 35 秒流したときのタイムコードはフレーム 1049（0:00:34:29）。
        // 現行換算は約 34.968s を返し、実経過（フレーム 1049 ÷ 29.97）≈ 35.002s との差は約 34ms。
        var tc = new LtcTimecode(0, 0, 34, 29, false);

        double result = tc.ToRealSeconds(30000.0 / 1001.0);

        result.Should().BeApproximately(34 + (29 / (30000.0 / 1001.0)), 1e-9);
        (1049 / (30000.0 / 1001.0)).Should().BeApproximately(35.0016, 0.0001);
        ((1049 / (30000.0 / 1001.0)) - result).Should().BeApproximately(0.0340, 0.0005);
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
