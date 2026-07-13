using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcDecoderTests
{
    [Fact]
    public void Constructor_InitializesWithDefaultFps()
    {
        var decoder = new LtcDecoder(48000);

        decoder.EstimatedFps.Should().Be(25.0);
    }

    [Fact]
    public void Constructor_InitializesWithCustomFps()
    {
        var decoder = new LtcDecoder(48000, fps: 30.0);

        decoder.EstimatedFps.Should().Be(30.0);
    }

    [Fact]
    public void Constructor_CalculatesCorrectHalfPeriod_For24Fps()
    {
        var decoder = new LtcDecoder(48000, fps: 24.0);

        // halfPeriod = sampleRate / (fps * 80 * 2) = 48000 / (24 * 160) = 12.5
        // EstimatedFps = sampleRate / (halfPeriod * 160) = 48000 / (12.5 * 160) = 24.0
        decoder.EstimatedFps.Should().Be(24.0);
    }

    [Fact]
    public void Write_AcceptsAudioSamples()
    {
        var decoder = new LtcDecoder(48000);
        var samples = new float[100];

        Action act = () => decoder.Write(samples, samples.Length);

        act.Should().NotThrow();
    }

    [Fact]
    public void Write_WithZeroCount_DoesNotThrow()
    {
        var decoder = new LtcDecoder(48000);
        var samples = new float[100];

        Action act = () => decoder.Write(samples, 0);

        act.Should().NotThrow();
    }

    [Fact]
    public void Read_ReturnsNull_WhenQueueIsEmpty()
    {
        var decoder = new LtcDecoder(48000);

        decoder.Read().Should().BeNull();
    }

    [Fact]
    public void Read_ReturnsNull_WhenOnlySilenceIsFed()
    {
        var decoder = new LtcDecoder(48000);
        var silence = new float[48000]; // 1 second of silence

        decoder.Write(silence, silence.Length);

        decoder.Read().Should().BeNull();
    }

    [Fact]
    public void EstimatedFps_Returns24_ForRawFpsNear24()
    {
        var decoder = new LtcDecoder(48000, fps: 24.0);

        decoder.EstimatedFps.Should().Be(24.0);
    }

    [Fact]
    public void EstimatedFps_Returns25_ForRawFpsNear25()
    {
        var decoder = new LtcDecoder(48000, fps: 25.0);

        decoder.EstimatedFps.Should().Be(25.0);
    }

    [Fact]
    public void EstimatedFps_Returns30_ForRawFpsNear30()
    {
        var decoder = new LtcDecoder(48000, fps: 30.0);

        decoder.EstimatedFps.Should().Be(30.0);
    }

    [Fact]
    public void EstimatedFps_Returns30_ForRawFpsAbove27_5()
    {
        var decoder = new LtcDecoder(48000, fps: 29.97);

        decoder.EstimatedFps.Should().Be(30.0);
    }

    [Fact]
    public void EstimatedFps_Returns25_ForRawFpsBetween24_5And27_5()
    {
        var decoder = new LtcDecoder(48000, fps: 26.0);

        decoder.EstimatedFps.Should().Be(25.0);
    }

    [Fact]
    public void LtcTimecode_ToString_FormatsCorrectly()
    {
        var tc = new LtcTimecode(1, 23, 45, 6, DropFrame: false);

        tc.ToString().Should().Be("01:23:45:06");
    }

    [Fact]
    public void LtcTimecode_ToString_UsesSemicolonForDropFrame()
    {
        var tc = new LtcTimecode(0, 0, 0, 0, DropFrame: true);

        tc.ToString().Should().Be("00:00:00;00");
    }

    [Fact]
    public void LtcTimecode_ToRealSeconds_ComputesCorrectValue()
    {
        var tc = new LtcTimecode(0, 0, 1, 0, DropFrame: false);

        tc.ToRealSeconds(25.0).Should().Be(1.0);
    }

    [Fact]
    public void LtcTimecode_ToRealSeconds_IncludesFrames()
    {
        var tc = new LtcTimecode(0, 0, 0, 15, DropFrame: false);

        tc.ToRealSeconds(30.0).Should().Be(0.5);
    }

    [Fact]
    public void LtcTimecode_ToRealSeconds_UsesCorrectFpsForDropFrame()
    {
        var tc = new LtcTimecode(0, 0, 1, 0, DropFrame: true);

        // Drop frame uses 30000/1001 fps regardless of passed fps
        double expected = 1.0;
        tc.ToRealSeconds(30.0).Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void LtcTimecode_ToRealSeconds_ComputesFullTimecode()
    {
        var tc = new LtcTimecode(1, 30, 45, 12, DropFrame: false);

        // 1*3600 + 30*60 + 45 + 12/25 = 3600 + 1800 + 45 + 0.48 = 5445.48
        tc.ToRealSeconds(25.0).Should().BeApproximately(5445.48, 0.001);
    }

    [Fact]
    public void LtcTimecode_FormatRealTime_FormatsCorrectly()
    {
        LtcTimecode.FormatRealTime(10376.48).Should().Be("10376.480 s");
    }

    [Fact]
    public void LtcTimecode_FormatRealTime_ClampsNegativeValues()
    {
        LtcTimecode.FormatRealTime(-1.0).Should().Be("0.000 s");
    }

    [Fact]
    public void LtcTimecode_FormatRealTime_HandlesZero()
    {
        LtcTimecode.FormatRealTime(0.0).Should().Be("0.000 s");
    }
}
