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

        LtcAudioSampleProcessingResult result = sampleProcessor.Process(
            e.Buffer,
            e.BytesRecorded,
            capture.WaveFormat);
        _audioCallbacks++;
        _samplesReceived += result.SampleCount;

        foreach (LtcFrameReceivedEventArgs frame in result.Frames)
        {
            _decodedFrames++;
            FrameReceived?.Invoke(this, frame);
        }

        LogStatsIfNeeded(capture.WaveFormat, result.Peak, result.Rms, result.EstimatedFps);
    }

    private void LogStatsIfNeeded(WaveFormat format, float peak, float rms, double estimatedFps)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastStatsLogAt < TimeSpan.FromSeconds(2))
            return;

        Log.Information(
            "LTC audio stats callbacks={Callbacks} samples={Samples} decodedFrames={Frames} sampleRate={SampleRate} bits={Bits} channels={Channels} peak={Peak:F3} rms={Rms:F3} decoderFps={DecoderFps:F3}",
            _audioCallbacks, _samplesReceived, _decodedFrames,
            format.SampleRate, format.BitsPerSample, format.Channels,
            peak, rms, estimatedFps);

        _audioCallbacks = 0;
        _samplesReceived = 0;
        _decodedFrames = 0;
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

public sealed record LtcFrameReceivedEventArgs(
    LtcTimecode Timecode,
    double Fps,
    double RealTimeSeconds);
