using NAudio.CoreAudioApi;
using NAudio.Wave;

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
        MMDevice? device = null;

        try
        {
            device = FindActiveDevice(DataFlow.Render, CableRenderDeviceNamePart);
            if (device == null)
            {
                skipReason = $"再生デバイス名に '{CableRenderDeviceNamePart}' を含むデバイスがありません。";
                return false;
            }

            player = new LtcSignalPlayer(device);
            device = null;
            return true;
        }
        catch (Exception ex)
        {
            skipReason = $"VB-CABLE 再生デバイスの初期化に失敗しました: {ex.Message}";
            return false;
        }
        finally
        {
            device?.Dispose();
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

    public void Play(
        LtcTimecode start,
        int fps,
        TimeSpan duration,
        LtcTestSignalGenerator.Options? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Stop();

        int frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * fps));
        IReadOnlyList<LtcTimecode> timecodes = BuildContinuousTimecodes(start, fps, frameCount);
        float[] monoSamples = LtcTestSignalGenerator.Generate(timecodes, fps, SampleRate, options);
        PlaySamples(monoSamples);
    }

    public void PlayWithSilence(
        LtcTimecode start,
        int fps,
        TimeSpan signalBefore,
        TimeSpan silence,
        TimeSpan signalAfter,
        LtcTestSignalGenerator.Options? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (signalBefore <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(signalBefore));
        if (silence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(silence));
        if (signalAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(signalAfter));

        Stop();

        int beforeFrameCount = Math.Max(1, (int)Math.Ceiling(signalBefore.TotalSeconds * fps));
        int silentFrameCount = Math.Max(1, (int)Math.Round(silence.TotalSeconds * fps));
        int afterFrameCount = Math.Max(1, (int)Math.Ceiling(signalAfter.TotalSeconds * fps));
        IReadOnlyList<LtcTimecode> beforeTimecodes =
            BuildContinuousTimecodes(start, fps, beforeFrameCount);
        LtcTimecode afterStart = AdvanceTimecode(start, fps, beforeFrameCount + silentFrameCount);
        IReadOnlyList<LtcTimecode> afterTimecodes =
            BuildContinuousTimecodes(afterStart, fps, afterFrameCount);

        float[] beforeSamples =
            LtcTestSignalGenerator.Generate(beforeTimecodes, fps, SampleRate, options);
        float[] afterSamples =
            LtcTestSignalGenerator.Generate(afterTimecodes, fps, SampleRate, options);
        int silenceSampleCount = Math.Max(1, (int)Math.Round(silence.TotalSeconds * SampleRate));
        var monoSamples = new float[beforeSamples.Length + silenceSampleCount + afterSamples.Length];
        Array.Copy(beforeSamples, monoSamples, beforeSamples.Length);
        Array.Copy(
            afterSamples,
            0,
            monoSamples,
            beforeSamples.Length + silenceSampleCount,
            afterSamples.Length);
        PlaySamples(monoSamples);
    }

    private void PlaySamples(float[] monoSamples)
    {
        float[] interleavedSamples = DuplicateToChannels(monoSamples, Channels);
        byte[] audioBytes = new byte[interleavedSamples.Length * sizeof(float)];
        Buffer.BlockCopy(interleavedSamples, 0, audioBytes, 0, audioBytes.Length);
        var provider = new BufferedWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            BufferLength = audioBytes.Length,
            ReadFully = true,
        };
        provider.AddSamples(audioBytes, 0, audioBytes.Length);
        var output = new WasapiOut(
            _device,
            AudioClientShareMode.Shared,
            useEventSync: true,
            latency: 100);

        try
        {
            output.Init(provider);
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

    internal static LtcTimecode AdvanceTimecode(
        LtcTimecode start,
        int fps,
        int frameCount)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (frameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        LtcTimecode current = start;
        for (int i = 0; i < frameCount; i++)
            current = LtcTestSignalGenerator.Increment(current, fps);

        return current;
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
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
        foreach (MMDevice device in devices)
        {
            try
            {
                if (device.FriendlyName.Contains(friendlyNamePart, StringComparison.OrdinalIgnoreCase))
                    return device;
            }
            catch
            {
                device.Dispose();
                throw;
            }

            device.Dispose();
        }

        return null;
    }

}
