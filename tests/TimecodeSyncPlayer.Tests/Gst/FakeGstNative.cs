using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>IGstNativeApi の記録用 fake。ネイティブ DLL に依存しない。</summary>
internal sealed class FakeGstNative : IGstNativeApi
{
    public IntPtr PlayerCreateResult { get; init; } = IntPtr.Zero;
    public string CreateError { get; init; } = "";
    public List<bool> SetPausedCalls { get; } = [];
    public List<(string Path, double? Start, bool Paused)> LoadCalls { get; } = [];
    public List<double> SeekCalls { get; } = [];
    public int LoadResult { get; set; }
    public string LoadError { get; set; } = "";
    public ulong SeekResult { get; set; } = 1;
    public int StopResult { get; set; }
    public int SetPausedResult { get; set; }
    public int SetSpeedResult { get; set; }
    public List<double> SetSpeedCalls { get; } = [];
    public int SetVolumeResult { get; set; }
    public List<double> SetVolumeCalls { get; } = [];
    public int SetMuteResult { get; set; }
    public List<bool> SetMuteCalls { get; } = [];
    public int SetFrameCallbackCalls { get; private set; }
    public GstNative.TcsFrameNotifyDelegate? LastFrameCallback { get; private set; }
    public double TimePos { get; set; }
    public double Duration { get; set; } = 10;
    public double Fps { get; set; } = 30;
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Decoder { get; set; } = "";
    public bool SpoutReadyValue { get; set; } = true;
    public int ConsumeUpdateResult { get; set; }
    public int ConsumeUpdateCalls { get; private set; }
    public int SetRateInstantResult { get; set; } = -1;
    public List<double> RateInstantCalls { get; } = [];

    public int SetRateInstant(IntPtr player, double rate)
    {
        RateInstantCalls.Add(rate);
        return SetRateInstantResult;
    }

    public IntPtr PlayerCreate(string senderName, out string error)
        => PlayerCreate(senderName, IntPtr.Zero, out error);

    public IntPtr PlayerCreate(string senderName, IntPtr externalDevice, out string error)
    {
        error = CreateError;
        LastExternalDevice = externalDevice;
        PlayerCreateCalls++;
        return PlayerCreateResult;
    }

    public IntPtr LastExternalDevice { get; private set; }
    public int PlayerCreateCalls { get; private set; }

    public void PlayerDestroy(IntPtr player) { }

    public int Load(IntPtr player, string path, double startSeconds, bool paused, out string error)
    {
        error = LoadError;
        LoadCalls.Add((path, startSeconds >= 0 ? startSeconds : null, paused));
        return LoadResult;
    }

    public int Stop(IntPtr player) => StopResult;

    public int SetPaused(IntPtr player, bool paused)
    {
        SetPausedCalls.Add(paused);
        return SetPausedResult;
    }

    public bool IsPausedValue { get; set; } = true;
    public bool IsPaused(IntPtr player) => IsPausedValue;

    public ulong Seek(IntPtr player, double seconds)
    {
        SeekCalls.Add(seconds);
        return SeekResult;
    }

    public ulong StepFrame(IntPtr player) => 1;
    public ulong GetGeneration(IntPtr player) => 7;
    public ulong SetGeneration(IntPtr player, ulong generation) => generation;
    public int SetSpeed(IntPtr player, double rate)
    {
        SetSpeedCalls.Add(rate);
        return SetSpeedResult;
    }
    public int SetVolume(IntPtr player, double volume0To100)
    {
        SetVolumeCalls.Add(volume0To100);
        return SetVolumeResult;
    }
    public int SetMute(IntPtr player, bool mute)
    {
        SetMuteCalls.Add(mute);
        return SetMuteResult;
    }
    public List<int> SetDecodeModeCalls { get; } = [];
    public int SetDecodeModeResult { get; set; }
    public int SetDecodeMode(IntPtr player, int mode)
    {
        SetDecodeModeCalls.Add(mode);
        return SetDecodeModeResult;
    }
    public bool TryGetTimePos(IntPtr player, out double seconds) { seconds = TimePos; return true; }
    public bool TryGetDuration(IntPtr player, out double seconds) { seconds = Duration; return true; }
    public bool TryGetFps(IntPtr player, out double fps) { fps = Fps; return true; }
    public string GetPath(IntPtr player) => Path;
    public bool TryGetSize(IntPtr player, out int width, out int height)
    {
        width = Width;
        height = Height;
        return width > 0 && height > 0;
    }
    public void SetFrameCallback(IntPtr player, GstNative.TcsFrameNotifyDelegate? callback)
    {
        SetFrameCallbackCalls++;
        LastFrameCallback = callback;
    }
    public int ConsumeUpdate(IntPtr player) { ConsumeUpdateCalls++; return ConsumeUpdateResult; }

    public int Acquire(IntPtr player, ulong generation, out GstNative.TcsFrameInfo info)
    {
        info = new GstNative.TcsFrameInfo
        {
            Generation = generation,
            Seq = 1,
            PtsNs = 0,
            Width = 64,
            Height = 64,
            IsGpu = 1,
            Slot = -1,
        };
        return 1;
    }

    public bool TryGetLeasedTexture(IntPtr player, out IntPtr texture, out uint subresource, out uint dxgiFormat)
    {
        texture = IntPtr.Zero;
        subresource = 0;
        dxgiFormat = 87;
        return false;
    }

    public void Release(IntPtr player) { }

    public int PublishSpoutVerification(IntPtr player) => 0;
    public int SendImage(IntPtr player, IntPtr bgra, int width, int height, int pitch) => 0;
    public string DecoderName(IntPtr player) => Decoder;
    public bool SpoutReady(IntPtr player) => SpoutReadyValue;

    public int DrainDeliveryEvents(IntPtr player, GstNative.TcsDeliveryEvent[] buffer, uint capacity, out uint count)
    {
        count = 0;
        return 0;
    }

    public ulong DeliveryArrivals { get; set; }
    public int GetDeliveryStats(IntPtr player, out GstNative.TcsDeliveryStats stats)
    {
        stats = new GstNative.TcsDeliveryStats { Arrivals = DeliveryArrivals };
        return 0;
    }

    public int GetRingInfo(IntPtr player, IntPtr[] handles, uint capacity, out uint count,
        out IntPtr fence, out int width, out int height)
    {
        count = 0;
        fence = IntPtr.Zero;
        width = 0;
        height = 0;
        return -3; // TCS_ERR_NO_FRAME: ring not created.
    }

    public int GetRingEpoch(IntPtr player, out uint epoch)
    {
        epoch = 0;
        return 0;
    }
}
