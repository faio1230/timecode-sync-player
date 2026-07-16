using NAudio.Wave;

namespace TimecodeSyncPlayer;

internal sealed class LtcAudioSampleProcessor
{
    private readonly LtcDecoder _decoder;
    private readonly List<LtcFrameReceivedEventArgs> _frameBuffer = [];

    internal LtcAudioSampleProcessor(LtcDecoder decoder)
    {
        _decoder = decoder;
    }

    internal LtcAudioSampleProcessingResult Process(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format)
    {
        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);
        (float peak, float rms) = MeasureLevel(samples);
        _decoder.Write(samples, samples.Length);

        _frameBuffer.Clear();
        LtcTimecode? timecode;
        while ((timecode = _decoder.Read()) != null)
        {
            double fps = _decoder.EstimatedFps;
            _frameBuffer.Add(new LtcFrameReceivedEventArgs(
                timecode,
                fps,
                timecode.ToRealSeconds(fps)));
        }

        IReadOnlyList<LtcFrameReceivedEventArgs> frames = _frameBuffer.Count == 0
            ? Array.Empty<LtcFrameReceivedEventArgs>()
            : _frameBuffer.ToArray();

        return new LtcAudioSampleProcessingResult(
            samples.Length,
            peak,
            rms,
            _decoder.EstimatedFps,
            frames);
    }

    private static (float Peak, float Rms) MeasureLevel(float[] samples)
    {
        if (samples.Length == 0)
            return (0f, 0f);

        double sumSquares = 0;
        float peak = 0;
        int finiteSampleCount = 0;
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
                continue;

            finiteSampleCount++;
            float abs = Math.Abs(sample);
            if (abs > peak)
                peak = abs;
            sumSquares += sample * sample;
        }

        return finiteSampleCount == 0
            ? (0f, 0f)
            : (peak, (float)Math.Sqrt(sumSquares / finiteSampleCount));
    }
}

internal sealed record LtcAudioSampleProcessingResult(
    int SampleCount,
    float Peak,
    float Rms,
    double EstimatedFps,
    IReadOnlyList<LtcFrameReceivedEventArgs> Frames);
