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
}
