using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed class LtcAudioMonitor : ILtcMonitor, IDisposable
{
    private WasapiCapture? _capture;
    private LtcAudioSampleProcessor? _sampleProcessor;
    private long _audioCallbacks;
    private long _samplesReceived;
    private long _decodedFrames;
    private readonly List<double> _frameDelaysMs = [];
    private double _anchorSpreadMs;
    private DateTime _lastStatsLogAt = DateTime.MinValue;
    private string? _deviceName;
    private int _sampleRate;

    public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived;
    public event EventHandler<Exception?>? Stopped;

    public bool IsRunning => _capture != null;
    public string? DeviceName => _deviceName;
    public int SampleRate => _sampleRate;

    public IReadOnlyList<string> GetCaptureDeviceNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .ToList();
    }

    public void Start(string? deviceName)
    {
        Stop();

        MMDevice? device = FindDevice(deviceName);
        if (device == null && deviceName != null)
        {
            Log.Warning("指定されたLTCデバイスが見つかりません: {DeviceName}。既定デバイスを使用します。", deviceName);
        }

        _capture = device != null ? new WasapiCapture(device) : new WasapiCapture();

        WaveFormat format = _capture.WaveFormat;
        _deviceName = deviceName;
        _sampleRate = format.SampleRate;
        _sampleProcessor = new LtcAudioSampleProcessor(new LtcDecoder(format.SampleRate, fps: 25.0));
        _audioCallbacks = 0;
        _samplesReceived = 0;
        _decodedFrames = 0;
        _frameDelaysMs.Clear();
        _anchorSpreadMs = 0;
        _lastStatsLogAt = DateTime.UtcNow;
        _capture.DataAvailable += OnAudioData;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();

        Log.Information("LTC monitor started device={Device} {Rate}Hz {Bits}bit {Channels}ch",
            deviceName, format.SampleRate, format.BitsPerSample, format.Channels);
    }

    public void Stop()
    {
        if (_capture == null)
            return;

        _capture.StopRecording();
        _capture.DataAvailable -= OnAudioData;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
        _sampleProcessor = null;
    }

    public void Dispose()
    {
        Stop();
        CleanupCapture();
    }

    private static MMDevice? FindDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(device => device.FriendlyName == deviceName);
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        var sampleProcessor = _sampleProcessor;
        if (capture == null || sampleProcessor == null || e.BytesRecorded <= 0)
            return;

        // T2: コールバック入口の QPC を 1 回だけ取り、フレーム終端時刻の換算に使う。
        long callbackTimestamp = Stopwatch.GetTimestamp();
        LtcAudioSampleProcessingResult result = sampleProcessor.Process(
            e.Buffer,
            e.BytesRecorded,
            capture.WaveFormat,
            callbackTimestamp);
        _audioCallbacks++;
        _samplesReceived += result.SampleCount;

        foreach (LtcFrameReceivedEventArgs frame in result.Frames)
        {
            _decodedFrames++;
            // 計測用（T2）: 受信ハンドラがフレーム終端からどれだけ遅れて動いたか。
            _frameDelaysMs.Add((Stopwatch.GetTimestamp() - frame.FrameEndTimestamp) * 1000.0 / Stopwatch.Frequency);
            FrameReceived?.Invoke(this, frame);
        }
        _anchorSpreadMs = result.AnchorSpreadMs;

        LogStatsIfNeeded(capture.WaveFormat, result.Peak, result.Rms, result.EstimatedFps);
    }

    private void LogStatsIfNeeded(WaveFormat format, float peak, float rms, double estimatedFps)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastStatsLogAt < TimeSpan.FromSeconds(2))
            return;

        // 直近 2 秒の (受信ハンドラ QPC − フレーム終端 QPC)。フレームが無ければ -1。
        double delayMedianMs = -1;
        double delayMaxMs = -1;
        if (_frameDelaysMs.Count > 0)
        {
            _frameDelaysMs.Sort();
            delayMedianMs = _frameDelaysMs[_frameDelaysMs.Count / 2];
            delayMaxMs = _frameDelaysMs[^1];
        }

        Log.Information(
            "LTC audio stats callbacks={Callbacks} samples={Samples} decodedFrames={Frames} sampleRate={SampleRate} bits={Bits} channels={Channels} peak={Peak:F3} rms={Rms:F3} decoderFps={DecoderFps:F3} frameDelayMedianMs={DelayMedian:F1} frameDelayMaxMs={DelayMax:F1} anchorSpreadMs={AnchorSpread:F1}",
            _audioCallbacks, _samplesReceived, _decodedFrames,
            format.SampleRate, format.BitsPerSample, format.Channels,
            peak, rms, estimatedFps, delayMedianMs, delayMaxMs, _anchorSpreadMs);

        _audioCallbacks = 0;
        _samplesReceived = 0;
        _decodedFrames = 0;
        _frameDelaysMs.Clear();
        _lastStatsLogAt = now;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        CleanupCapture();
        Stopped?.Invoke(this, e.Exception);
    }

    private void CleanupCapture()
    {
        if (_capture == null)
            return;

        _capture.DataAvailable -= OnAudioData;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
        _sampleProcessor = null;
    }
}

/// <summary>
/// デコードされた 1 フレーム。T2 でサンプル位置由来の時刻を追加した（既存の 3 項目はそのまま）。
/// <paramref name="FrameEndTimestamp"/> は同期ワードの最後のビットを確定させた遷移の QPC、
/// <paramref name="CallbackTimestamp"/> はそのフレームを積んだ音声コールバック入口の QPC。
/// どちらも <see cref="System.Diagnostics.Stopwatch.Frequency"/> と同じ刻み。
/// </summary>
public sealed record LtcFrameReceivedEventArgs(
    LtcTimecode Timecode,
    double Fps,
    double RealTimeSeconds,
    long FrameEndTimestamp = 0,
    long CallbackTimestamp = 0,
    long EndSampleIndex = 0,
    double AnchorSpreadMs = 0);
