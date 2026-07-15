using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// テスト用 LTC 波形を指定した WASAPI 再生デバイスへ送出する。
/// </summary>
internal sealed class LtcSignalPlayer : IDisposable
{
    private const string CableRenderDeviceNamePart = "CABLE Input";
    private const string CableCaptureDeviceNamePart = "CABLE Output";
    private readonly MMDevice _device;
    private WasapiOut? _output;
    private bool _disposed;

    private LtcSignalPlayer(MMDevice device)
    {
        _device = device;
        SampleRate = device.AudioClient.MixFormat.SampleRate;
        Channels = device.AudioClient.MixFormat.Channels;
        DeviceName = device.FriendlyName;
    }

    public string DeviceName { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public static bool TryCreateCablePlayer(
        out LtcSignalPlayer? player,
        out string? skipReason)
    {
        player = null;
        skipReason = null;

        try
        {
            MMDevice? device = FindActiveDevice(DataFlow.Render, CableRenderDeviceNamePart);
            if (device == null)
            {
                skipReason = $"再生デバイス名に '{CableRenderDeviceNamePart}' を含むデバイスがありません。";
                return false;
            }

            player = new LtcSignalPlayer(device);
            return true;
        }
        catch (Exception ex)
        {
            skipReason = $"VB-CABLE 再生デバイスの初期化に失敗しました: {ex.Message}";
            return false;
        }
    }

    public static string? FindCableCaptureDeviceName()
    {
        MMDevice? device = FindActiveDevice(DataFlow.Capture, CableCaptureDeviceNamePart);
        if (device == null)
            return null;

        try
        {
            return device.FriendlyName;
        }
        finally
        {
            device.Dispose();
        }
    }

    public void Play(LtcTimecode start, int fps, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Stop();

        int frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * fps));
        IReadOnlyList<LtcTimecode> timecodes = BuildContinuousTimecodes(start, fps, frameCount);
        float[] monoSamples = LtcTestSignalGenerator.Generate(timecodes, fps, SampleRate);
        float[] interleavedSamples = DuplicateToChannels(monoSamples, Channels);
        var provider = new ArraySampleProvider(interleavedSamples, SampleRate, Channels);
        var output = new WasapiOut(
            _device,
            AudioClientShareMode.Shared,
            useEventSync: true,
            latency: 100);

        try
        {
            output.Init(new SampleToWaveProvider(provider));
            output.Play();
            _output = output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        WasapiOut? output = _output;
        _output = null;
        if (output == null)
            return;

        try
        {
            output.Stop();
        }
        finally
        {
            output.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            Stop();
        }
        finally
        {
            _device.Dispose();
        }
    }

    internal static IReadOnlyList<LtcTimecode> BuildContinuousTimecodes(
        LtcTimecode start,
        int fps,
        int frameCount)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        var frames = new List<LtcTimecode>(frameCount);
        LtcTimecode current = start;
        for (int i = 0; i < frameCount; i++)
        {
            frames.Add(current);
            current = LtcTestSignalGenerator.Increment(current, fps);
        }

        return frames;
    }

    internal static float[] DuplicateToChannels(float[] monoSamples, int channels)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        var result = new float[monoSamples.Length * channels];
        for (int frame = 0; frame < monoSamples.Length; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
                result[frame * channels + channel] = monoSamples[frame];
        }

        return result;
    }

    private static MMDevice? FindActiveDevice(DataFlow dataFlow, string friendlyNamePart)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(dataFlow, DeviceState.Active)
            .FirstOrDefault(device =>
                device.FriendlyName.Contains(friendlyNamePart, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        internal ArraySampleProvider(float[] samples, int sampleRate, int channels)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
