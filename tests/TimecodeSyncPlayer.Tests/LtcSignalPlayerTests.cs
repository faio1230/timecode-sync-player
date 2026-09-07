using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalPlayerTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    public void HeldSignal_HasRealAudioDurationAndDecodesToRepeatedTarget(int fps)
    {
        float[] samples = LtcSignalPlayer.BuildHeldSamples(440, fps, TimeSpan.FromSeconds(1), 48000);
        samples.Length.Should().Be((int)Math.Round((1 + 5d / fps) * 48000));
        var decoder = new LtcDecoder(48000, fps);
        decoder.Write(samples, samples.Length);
        var decoded = new List<LtcTimecode>();
        while (decoder.Read() is { } frame) decoded.Add(frame);
        decoded.Count.Should().BeGreaterThanOrEqualTo(fps);
        decoded.TakeLast(fps - 1).Should().OnlyContain(t => t == new LtcTimecode(0, 7, 20, 0, false));
        decoded.Should().Contain(t => t.ToRealSeconds(fps) < 440);
    }

    [Fact]
    public void AdvanceTimecode_SkipsElapsedSilentFrames()
    {
        var start = new LtcTimecode(1, 2, 3, 20, false);

        LtcTimecode result = LtcSignalPlayer.AdvanceTimecode(start, fps: 25, frameCount: 45);

        result.Should().Be(new LtcTimecode(1, 2, 5, 15, false));
    }

    [Fact]
    public void AdvanceTimecode_WithZeroFramesReturnsStart()
    {
        var start = new LtcTimecode(23, 59, 59, 24, false);

        LtcSignalPlayer.AdvanceTimecode(start, fps: 25, frameCount: 0).Should().Be(start);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, -1)]
    public void AdvanceTimecode_WithInvalidArgumentThrows(int fps, int frameCount)
    {
        Action act = () => LtcSignalPlayer.AdvanceTimecode(
            new LtcTimecode(0, 0, 0, 0, false),
            fps,
            frameCount);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
