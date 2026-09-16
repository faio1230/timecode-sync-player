using System.Diagnostics;
using NAudio.Wave;

namespace TimecodeSyncPlayer;

internal sealed class LtcAudioSampleProcessor
{
    /// <summary>T2: コールバックのオフセット最小値を保つ窓の長さ（約 40 コールバック）。</summary>
    internal static readonly TimeSpan AnchorWindow = TimeSpan.FromSeconds(2);

    private readonly LtcDecoder _decoder;
    private readonly Func<long> _getQpc;
    private readonly LtcAnchorFilter _anchor;
    private readonly List<LtcFrameReceivedEventArgs> _frameBuffer = [];
    private long _totalSamples;

    internal LtcAudioSampleProcessor(LtcDecoder decoder, Func<long>? qpcProvider = null)
    {
        _decoder = decoder;
        _getQpc = qpcProvider ?? Stopwatch.GetTimestamp;
        _anchor = new LtcAnchorFilter(decoder.SampleRate, AnchorWindow.TotalSeconds);
    }

    internal LtcAudioSampleProcessingResult Process(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format)
        => Process(buffer, bytesRecorded, format, _getQpc());

    /// <summary>
    /// T2: callbackTimestamp はコールバック入口の QPC（1 コールバックにつき 1 回）。
    /// フレーム終端の時刻は「直近 2 秒のオフセット最小値」をアンカーとしてサンプル位置から出す。
    /// </summary>
    internal LtcAudioSampleProcessingResult Process(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format,
        long callbackTimestamp)
    {
        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);
        (float peak, float rms) = MeasureLevel(samples);
        _decoder.Write(samples, samples.Length);
        _totalSamples += samples.Length;
        _anchor.Add(callbackTimestamp, _totalSamples);

        double anchorSpreadMs = _anchor.SpreadTicks * 1000.0 / Stopwatch.Frequency;
        _frameBuffer.Clear();
        LtcDecodedFrame? decoded;
        while ((decoded = _decoder.Read()) != null)
        {
            double fps = _decoder.EstimatedFps;
            _frameBuffer.Add(new LtcFrameReceivedEventArgs(
                decoded.Timecode,
                fps,
                decoded.Timecode.ToRealSeconds(fps),
                FrameEndTimestamp: _anchor.AnchorTicks + _anchor.SamplesToTicks(decoded.EndSampleIndex),
                CallbackTimestamp: callbackTimestamp,
                EndSampleIndex: decoded.EndSampleIndex,
                AnchorSpreadMs: anchorSpreadMs));
        }

        IReadOnlyList<LtcFrameReceivedEventArgs> frames = _frameBuffer.Count == 0
            ? Array.Empty<LtcFrameReceivedEventArgs>()
            : _frameBuffer.ToArray();

        return new LtcAudioSampleProcessingResult(
            samples.Length,
            peak,
            rms,
            _decoder.EstimatedFps,
            frames,
            anchorSpreadMs);
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

/// <summary>
/// T2: 音声コールバックのスケジューリング遅れ（遅れる方向にしか揺れない）を除く最小値フィルタ。
/// コールバック k の offset_k = Q_k − N_k × F / sampleRate を計算し、直近 2 秒の最小値を anchor とする。
/// 窓から古い値が抜れたら残りの最小値へ更新する（周波数ドリフトに追従するため単調ではない）。
/// 音声スレッドから呼ばれるため、固定長の窓を線形に走査するだけにしてロックも確保もしない。
/// </summary>
internal sealed class LtcAnchorFilter
{
    private readonly Queue<(long Qpc, long OffsetTicks)> _window = new();
    private readonly int _sampleRate;
    private readonly long _windowTicks;

    internal LtcAnchorFilter(int sampleRate, double windowSeconds)
    {
        _sampleRate = sampleRate;
        _windowTicks = (long)Math.Round(Stopwatch.Frequency * windowSeconds);
    }

    /// <summary>直近 2 秒の offset_k の最小値（QPC 単位）。</summary>
    internal long AnchorTicks { get; private set; }

    /// <summary>窓内の offset_k の最大 − 最小（QPC 単位）。</summary>
    internal long SpreadTicks { get; private set; }

    internal void Add(long callbackQpc, long totalSamples)
    {
        long offsetTicks = callbackQpc - SamplesToTicks(totalSamples);
        _window.Enqueue((callbackQpc, offsetTicks));

        long cutoff = callbackQpc - _windowTicks;
        while (_window.Count > 0 && _window.Peek().Qpc < cutoff)
            _window.Dequeue();

        long min = long.MaxValue;
        long max = long.MinValue;
        foreach ((long _, long windowOffset) in _window)
        {
            if (windowOffset < min) min = windowOffset;
            if (windowOffset > max) max = windowOffset;
        }

        AnchorTicks = min;
        SpreadTicks = max - min;
    }

    internal long SamplesToTicks(long samples) =>
        (long)Math.Round(samples * (double)Stopwatch.Frequency / _sampleRate);
}

internal sealed record LtcAudioSampleProcessingResult(
    int SampleCount,
    float Peak,
    float Rms,
    double EstimatedFps,
    IReadOnlyList<LtcFrameReceivedEventArgs> Frames,
    double AnchorSpreadMs);
