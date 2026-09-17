using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalPlayerTests
{
    [Fact]
    public void BuildHeldTimecodes_AdvancesMonotonicallyFromThePreviousFrame()
    {
        // 連続再生が 11.92（frame 298）まで進んだ後の保持 12.0。
        // 前置きは直前の次（11.96）から始まり、保持値 12.00 まで単調に進む
        // （固定の 5 フレーム前置き 11.80 だと Reverse が 1 枚出る）。
        IReadOnlyList<LtcTimecode> frames = LtcSignalPlayer.BuildHeldTimecodes(
            12.0, 25, TimeSpan.FromSeconds(2), previousFrame: 298);

        int[] numbers = frames.Select(tc => FrameNumber(tc, 25)).ToArray();
        numbers[0].Should().Be(299);
        numbers.Should().Contain(300);
        for (int i = 1; i < numbers.Length; i++)
            numbers[i].Should().BeGreaterThanOrEqualTo(numbers[i - 1], "連続 → 保持の切替でフレーム番号が戻らない");
    }

    private static int FrameNumber(LtcTimecode timecode, int fps) =>
        ((timecode.Hours * 60 + timecode.Minutes) * 60 + timecode.Seconds) * fps + timecode.Frames;

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
        while (decoder.Read() is { } frame) decoded.Add(frame.Timecode);
        decoded.Count.Should().BeGreaterThanOrEqualTo(fps);
        decoded.TakeLast(fps - 1).Should().OnlyContain(t => t == new LtcTimecode(0, 7, 20, 0, false));
        decoded.Should().Contain(t => t.ToRealSeconds(fps) < 440);
    }

    [Fact]
    public void HeldSignal_At29_97_UsesNominalFrameBaseForTimecode()
    {
        const double fps = 30000.0 / 1001.0;
        float[] samples = LtcSignalPlayer.BuildHeldSamples(440, fps, TimeSpan.FromSeconds(1), 48000);

        // 29.97 のタイムコード番号はノミナル 30 進み（ノンドロップ）。1 秒ぶんは ceil(29.97)=30 フレーム。
        samples.Length.Should().Be((int)Math.Round((5 + Math.Ceiling(fps)) * 48000 / fps));
        var decoder = new LtcDecoder(48000, fps);
        decoder.Write(samples, samples.Length);
        var decoded = new List<LtcTimecode>();
        while (decoder.Read() is { } frame) decoded.Add(frame.Timecode);
        decoded.Count.Should().BeGreaterThanOrEqualTo(30);
        decoded.TakeLast(29).Should().OnlyContain(t => t == new LtcTimecode(0, 7, 20, 0, false));
    }

    [Fact]
    public void ContinuousTimecodes_At29_97_AdvanceByNominalThirtyFrames()
    {
        var frames = LtcSignalPlayer.BuildContinuousTimecodes(
            new LtcTimecode(0, 0, 0, 0, false), 30000.0 / 1001.0, 31);

        frames.Should().HaveCount(31);
        frames[^1].Should().Be(new LtcTimecode(0, 0, 1, 0, false));
    }

    [Fact]
    public void AdvanceTimecode_At29_97_CountsNominalFrames()
    {
        LtcTimecode result = LtcSignalPlayer.AdvanceTimecode(
            new LtcTimecode(0, 0, 0, 0, false), 30000.0 / 1001.0, 90);

        result.Should().Be(new LtcTimecode(0, 0, 3, 0, false));
    }

    [Fact]
    public void ContinuousTimecodes_WithUnsupportedFpsThrows()
    {
        Action act = () => LtcSignalPlayer.BuildContinuousTimecodes(
            new LtcTimecode(0, 0, 0, 0, false), 23.976, 10);

        act.Should().Throw<ArgumentOutOfRangeException>();
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
