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
    // 直前に生成した最後のフレーム番号。連続 → 保持の切替で前置きが戻らないようにする。
    private int? _lastFrame;

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
        double fps,
        TimeSpan duration,
        LtcTestSignalGenerator.Options? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFps(fps);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Stop();

        int frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * fps));
        IReadOnlyList<LtcTimecode> timecodes = BuildContinuousTimecodes(start, fps, frameCount);
        float[] monoSamples = LtcTestSignalGenerator.Generate(timecodes, fps, SampleRate, options);
        PlaySamples(monoSamples);
        _lastFrame = LastFrameNumber(timecodes, fps);
    }

    public void PlayWithSilence(
        LtcTimecode start,
        double fps,
        TimeSpan signalBefore,
        TimeSpan silence,
        TimeSpan signalAfter,
        LtcTestSignalGenerator.Options? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFps(fps);
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
        _lastFrame = LastFrameNumber(afterTimecodes, fps);
    }

    /// <summary>
    /// M6: 事前に生成・加工・リサンプル済みの波形（デバイスのミックスレート）をそのまま送出する。
    /// </summary>
    public void SendSamples(float[] monoSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (monoSamples.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(monoSamples));
        PlaySamples(monoSamples);
    }

    public void PlayHeld(double seconds, double fps, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        float[] samples = BuildHeldSamples(seconds, fps, duration, SampleRate, _lastFrame);
        Stop();
        PlaySamples(samples);
        _lastFrame = (int)Math.Round(seconds * NominalFps(fps));
    }

    /// <summary>
    /// 保持のフレーム列。前置きは「前回のフレームの次」から保持値まで進む（連続再生から保持へ
    /// 切り替えるとき、固定の 5 フレーム前置きだと直前の位置より戻って Reverse が 1 枚出る）。
    /// previousFrame が無いときは従来どおり保持値の 5 フレーム前から。
    /// </summary>
    internal static IReadOnlyList<LtcTimecode> BuildHeldTimecodes(
        double seconds, double fps, TimeSpan duration, int? previousFrame)
    {
        int nominalFps = NominalFps(fps);
        int targetFrame = (int)Math.Round(seconds * nominalFps);
        int firstPrelude = targetFrame - 5;
        if (previousFrame is int previous)
            firstPrelude = Math.Min(targetFrame, Math.Max(firstPrelude, previous + 1));
        var frames = new List<LtcTimecode>();
        // A short advancing prelude reacquires a jumped signal, followed by duplicate timecodes.
        for (int frame = firstPrelude; frame < targetFrame; frame++) frames.Add(FromFrame(frame, fps));
        frames.AddRange(Enumerable.Repeat(FromFrame(targetFrame, fps), (int)Math.Ceiling(duration.TotalSeconds * fps)));
        return frames;
    }

    internal static float[] BuildHeldSamples(
        double seconds, double fps, TimeSpan duration, int sampleRate, int? previousFrame = null)
    {
        if (!double.IsFinite(seconds) || seconds < 1 || seconds >= 24 * 3600)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!IsSupportedFps(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (duration < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return LtcTestSignalGenerator.Generate(
            BuildHeldTimecodes(seconds, fps, duration, previousFrame), fps: fps, sampleRate: sampleRate);
    }

    private static int LastFrameNumber(IReadOnlyList<LtcTimecode> timecodes, double fps)
    {
        LtcTimecode last = timecodes[^1];
        int nominalFps = NominalFps(fps);
        return ((last.Hours * 60 + last.Minutes) * 60 + last.Seconds) * nominalFps + last.Frames;
    }

    private static LtcTimecode FromFrame(int frame, double fps)
    {
        int nominalFps = NominalFps(fps);
        return new(
            frame / (nominalFps * 3600), frame / (nominalFps * 60) % 60, frame / nominalFps % 60, frame % nominalFps, false);
    }

    /// <summary>
    /// LTC の fps は 24 / 25 / 29.97(30000/1001) / 30 のみ。
    /// 29.97 のタイムコード番号はノンドロップのノミナル 30 で進む。
    /// </summary>
    internal static void ValidateFps(double fps)
    {
        if (!IsSupportedFps(fps))
            throw new ArgumentOutOfRangeException(nameof(fps));
    }

    private static bool IsSupportedFps(double fps) =>
        Math.Abs(fps - 24.0) < 0.01 || Math.Abs(fps - 25.0) < 0.01 ||
        Math.Abs(fps - 30.0) < 0.01 || Math.Abs(fps - (30000.0 / 1001.0)) < 0.01;

    private static int NominalFps(double fps) =>
        Math.Abs(fps - (30000.0 / 1001.0)) < 0.01 ? 30 : (int)Math.Round(fps);

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
        double fps,
        int frameCount)
    {
        ValidateFps(fps);
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        int nominalFps = NominalFps(fps);
        var frames = new List<LtcTimecode>(frameCount);
        LtcTimecode current = start;
        for (int i = 0; i < frameCount; i++)
        {
            frames.Add(current);
            current = LtcTestSignalGenerator.Increment(current, nominalFps);
        }

        return frames;
    }

    internal static LtcTimecode AdvanceTimecode(
        LtcTimecode start,
        double fps,
        int frameCount)
    {
        ValidateFps(fps);
        if (frameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        int nominalFps = NominalFps(fps);
        LtcTimecode current = start;
        for (int i = 0; i < frameCount; i++)
            current = LtcTestSignalGenerator.Increment(current, nominalFps);

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
