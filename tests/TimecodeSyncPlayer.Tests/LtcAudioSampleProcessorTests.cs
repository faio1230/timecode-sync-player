using System.Diagnostics;
using FluentAssertions;
using NAudio.Wave;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class LtcAudioSampleProcessorTests
{
    [Fact]
    public void Process_AllNonFiniteFloatSamples_ReturnsZeroLevels()
    {
        const int sampleRate = 48_000;
        float[] samples = [float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, 25));

        LtcAudioSampleProcessingResult result = processor.Process(
            ToBytes(samples),
            samples.Length * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        result.Peak.Should().Be(0);
        result.Rms.Should().Be(0);
    }

    [Fact]
    public void Process_MixedFiniteAndNonFiniteFloatSamples_MeasuresOnlyFiniteSamples()
    {
        const int sampleRate = 48_000;
        float[] samples =
        [
            float.NaN,
            0.5f,
            float.PositiveInfinity,
            -1.0f,
            float.NegativeInfinity,
        ];
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, 25));

        LtcAudioSampleProcessingResult result = processor.Process(
            ToBytes(samples),
            samples.Length * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        result.SampleCount.Should().Be(samples.Length,
            "non-finite samples remain on the decoder supply path");
        result.Peak.Should().Be(1.0f);
        result.Rms.Should().BeApproximately(
            (float)Math.Sqrt((0.25 + 1.0) / 2),
            0.000001f);
    }

    [Fact]
    public void Process_FloatPcmMeasuresSampleCountPeakAndRms()
    {
        const int sampleRate = 48000;
        float[] samples = [0.5f, -1.0f, 0.25f];
        var decoder = new LtcDecoder(sampleRate, 25);
        var processor = new LtcAudioSampleProcessor(decoder);

        LtcAudioSampleProcessingResult result = processor.Process(
            ToBytes(samples),
            samples.Length * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        result.SampleCount.Should().Be(3);
        result.Peak.Should().Be(1.0f);
        result.Rms.Should().BeApproximately(
            (float)Math.Sqrt((0.25 + 1.0 + 0.0625) / 3),
            0.000001f);
        result.EstimatedFps.Should().Be(decoder.EstimatedFps);
        result.Frames.Should().BeEmpty();
    }

    [Fact]
    public void Process_EmptyInputReturnsZeroLevelsAndNoFrames()
    {
        const int sampleRate = 48000;
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, 25));

        LtcAudioSampleProcessingResult result = processor.Process(
            [],
            0,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        result.SampleCount.Should().Be(0);
        result.Peak.Should().Be(0);
        result.Rms.Should().Be(0);
        result.Frames.Should().BeEmpty();
    }

    [Fact]
    public void Process_NoDecodedFramesReusesSharedEmptyResult()
    {
        const int sampleRate = 48000;
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, 25));
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        LtcAudioSampleProcessingResult first = processor.Process([], 0, format);
        LtcAudioSampleProcessingResult second = processor.Process([], 0, format);

        ReferenceEquals(first.Frames, second.Frames).Should().BeTrue();
    }

    [Fact]
    public void Process_GeneratedLtcPcmProducesFrameEventArguments()
    {
        const int sampleRate = 48000;
        const int fps = 25;
        var timecode = new LtcTimecode(1, 2, 3, 4, DropFrame: false);
        float[] samples = LtcTestSignalGenerator.Generate(
            [timecode, timecode, timecode, timecode],
            fps,
            sampleRate);
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, fps));

        LtcAudioSampleProcessingResult result = processor.Process(
            ToBytes(samples),
            samples.Length * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        result.SampleCount.Should().Be(samples.Length);
        result.Peak.Should().Be(1);
        result.Rms.Should().BeApproximately(1, 0.000001f);
        result.EstimatedFps.Should().Be(fps);
        result.Frames.Should().NotBeEmpty();
        result.Frames.Should().OnlyContain(frame => frame.Timecode == timecode);
        result.Frames.Should().OnlyContain(frame => frame.Fps == fps);
        result.Frames.Should().OnlyContain(frame =>
            Math.Abs(frame.RealTimeSeconds - timecode.ToRealSeconds(fps)) < 0.000001);
    }

    [Fact]
    public void Process_ConsecutiveChunksPreservesDecoderStateAndFrameOrder()
    {
        const int sampleRate = 48000;
        const int fps = 25;
        var first = new LtcTimecode(10, 20, 30, 0, DropFrame: false);
        var expected = Enumerable.Range(0, 8)
            .Aggregate(
                new List<LtcTimecode>(),
                (frames, _) =>
                {
                    frames.Add(frames.Count == 0 ? first : LtcTestSignalGenerator.Increment(frames[^1], fps));
                    return frames;
                });
        float[] samples = LtcTestSignalGenerator.Generate(expected, fps, sampleRate);
        int split = samples.Length / 2;
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, fps));

        LtcAudioSampleProcessingResult firstResult = processor.Process(
            ToBytes(samples[..split]),
            split * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        LtcAudioSampleProcessingResult secondResult = processor.Process(
            ToBytes(samples[split..]),
            (samples.Length - split) * sizeof(float),
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

        var decoded = firstResult.Frames.Concat(secondResult.Frames).Select(frame => frame.Timecode).ToList();
        decoded.Should().NotBeEmpty();
        int offset = expected.FindIndex(timecode => timecode == decoded[0]);
        offset.Should().BeGreaterThanOrEqualTo(0);
        decoded.Should().Equal(expected.Skip(offset).Take(decoded.Count));
    }

    private static byte[] ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // ── T2: コールバック時刻とサンプル位置からのフレーム終端時刻 ─────

    [Fact]
    public void Process_FrameEndTimestamp_IsAnchorPlusSampleOffset()
    {
        const int sampleRate = 48000;
        const int fps = 25;
        const int chunkSamples = 2400;   // 50ms。25fps の 1 フレーム（40ms）より長い
        const int frameCount = 8;
        float[] samples = LtcTestSignalGenerator.Generate(
            BuildContinuousFrames(new LtcTimecode(0, 0, 0, 0, false), fps, frameCount), fps, sampleRate);
        long ticksPerChunk = SamplesToTicks(chunkSamples, sampleRate);
        long baseOffset = 987_654_321;
        long now = baseOffset + ticksPerChunk;
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, fps), () => now);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        LtcAudioSampleProcessingResult first = processor.Process(
            ToBytes(samples[..chunkSamples]), chunkSamples * sizeof(float), format);
        now = baseOffset + (2 * ticksPerChunk);
        LtcAudioSampleProcessingResult second = processor.Process(
            ToBytes(samples[chunkSamples..(2 * chunkSamples)]), chunkSamples * sizeof(float), format);

        first.Frames.Should().NotBeEmpty();
        second.Frames.Should().NotBeEmpty("2 回目のコールバックの中で次のフレームが終端する");
        foreach (LtcFrameReceivedEventArgs frame in first.Frames)
        {
            frame.CallbackTimestamp.Should().Be(baseOffset + ticksPerChunk);
            frame.FrameEndTimestamp.Should().Be(baseOffset + SamplesToTicks(frame.EndSampleIndex, sampleRate));
        }
        foreach (LtcFrameReceivedEventArgs frame in second.Frames)
        {
            frame.CallbackTimestamp.Should().Be(baseOffset + (2 * ticksPerChunk));
            frame.FrameEndTimestamp.Should().Be(baseOffset + SamplesToTicks(frame.EndSampleIndex, sampleRate));
        }
    }

    [Fact]
    public void AnchorFilter_LateCallback_DoesNotMoveAnchor()
    {
        const int sampleRate = 48000;
        var filter = new LtcAnchorFilter(sampleRate, windowSeconds: 2.0);
        long baseOffset = 5_000_000;

        filter.Add(baseOffset + SamplesToTicks(2400, sampleRate), 2400);
        filter.Add(baseOffset + SamplesToTicks(4800, sampleRate), 4800);
        long normalAnchor = filter.AnchorTicks;
        normalAnchor.Should().Be(baseOffset);

        // 60ms 遅れて届いたコールバックは最小値を動かさない。
        long lateQpc = baseOffset + SamplesToTicks(7200, sampleRate) + SamplesToTicks(2880, sampleRate);
        filter.Add(lateQpc, 7200);

        filter.AnchorTicks.Should().Be(normalAnchor);
        filter.SpreadTicks.Should().Be(SamplesToTicks(2880, sampleRate));
    }

    [Fact]
    public void AnchorFilter_EvictedMinimum_UpdatesToNextMinimum()
    {
        const int sampleRate = 48000;
        var filter = new LtcAnchorFilter(sampleRate, windowSeconds: 0.5);
        long firstOffset = 5_000_000;
        long secondOffset = firstOffset + 1000;   // 時計ドリフト相当

        for (int k = 1; k <= 20; k++)
            filter.Add(firstOffset + SamplesToTicks(k * 2400, sampleRate), k * 2400);
        filter.AnchorTicks.Should().Be(firstOffset);

        for (int k = 21; k <= 25; k++)
            filter.Add(secondOffset + SamplesToTicks(k * 2400, sampleRate), k * 2400);
        filter.AnchorTicks.Should().Be(firstOffset, "古い最小値がまだ窓に残っている");

        for (int k = 26; k <= 40; k++)
            filter.Add(secondOffset + SamplesToTicks(k * 2400, sampleRate), k * 2400);
        filter.AnchorTicks.Should().Be(secondOffset, "窓から抜けたら残りの最小値へ更新される");
    }

    private static long SamplesToTicks(long samples, int sampleRate) =>
        (long)Math.Round(samples * (double)Stopwatch.Frequency / sampleRate);

    private static List<LtcTimecode> BuildContinuousFrames(LtcTimecode first, int fps, int count)
    {
        var frames = new List<LtcTimecode> { first };
        for (int i = 1; i < count; i++)
            frames.Add(LtcTestSignalGenerator.Increment(frames[^1], fps));
        return frames;
    }
}
