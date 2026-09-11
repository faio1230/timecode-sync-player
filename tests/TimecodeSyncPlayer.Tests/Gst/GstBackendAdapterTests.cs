using System.Runtime.InteropServices;
using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

public class GstCommandTranslatorTests
{
    [Fact]
    public void LoadFile_WithoutStart_IsParsedWithPlainPath()
    {
        var op = GstCommandTranslator.Translate(
            MpvPlaybackCommandBuilder.BuildLoadFileCommand(@"C:\media\a b.mp4", null));

        op.Should().BeOfType<GstLoadFileOperation>();
        var load = (GstLoadFileOperation)op;
        load.Path.Should().Be("C:/media/a b.mp4");
        load.StartSeconds.Should().BeNull();
    }

    [Fact]
    public void LoadFile_WithStartAndEscapes_IsParsed()
    {
        // buildLoadFileCommand のエスケープ出力を再現
        string cmd = "no-osd loadfile \"C:\\\\shots\\\"weird\\\".mp4\" replace -1 start=3.250000";
        var op = GstCommandTranslator.Translate(cmd);

        op.Should().BeOfType<GstLoadFileOperation>();
        var load = (GstLoadFileOperation)op;
        load.Path.Should().Be("C:\\shots\"weird\".mp4");
        load.StartSeconds.Should().Be(3.25);
    }

    [Theory]
    [InlineData("seek 12.500 absolute+exact", 12.5, false)]
    [InlineData("no-osd seek 4.250 absolute+exact", 4.25, false)]
    [InlineData("seek 3 relative+exact", 3.0, true)]
    public void Seek_IsParsedWithMode(string command, double seconds, bool relative)
    {
        var op = GstCommandTranslator.Translate(command);

        op.Should().BeOfType<GstSeekOperation>();
        var seek = (GstSeekOperation)op;
        seek.Seconds.Should().BeApproximately(seconds, 1e-9);
        seek.Relative.Should().Be(relative);
    }

    [Fact]
    public void StopAndFrameStep_AreRecognized()
    {
        GstCommandTranslator.Translate("stop").Should().Be(GstStopOperation.Instance);
        GstCommandTranslator.Translate("no-osd frame-step").Should().Be(GstFrameStepOperation.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nosuchcommand 1")]
    [InlineData("loadfile unquoted.mp4 replace")]
    [InlineData("seek abc absolute+exact")]
    public void UnknownOrMalformedCommands_AreIgnored(string command)
    {
        var op = GstCommandTranslator.Translate(command);
        op.Should().BeOfType<GstIgnoredOperation>();
    }
}

public class GstMpvApiAdapterTests
{
    [Fact]
    public void Create_WhenPlayerEnsureFails_ReturnsZero()
    {
        var native = new FakeGstNative { PlayerCreateResult = IntPtr.Zero };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);

        api.Create().Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void Pause_PropertyMirrorsAndForwards()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(0x42) };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.SetPropertyString(ctx, "pause", "no");
        state.IsPaused.Should().BeFalse();
        native.SetPausedCalls.Should().Equal(false);

        api.SetPropertyString(ctx, "pause", "yes");
        state.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void UnknownProperties_AreAcceptedAsNoops()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.SetPropertyString(ctx, "vo", "libmpv").Should().Be(0);
        api.SetPropertyString(ctx, "osd-msg3", "x").Should().Be(0);
        native.SetPausedCalls.Should().BeEmpty();
    }

    [Fact]
    public void GetProperty_TimePosAndDurationAndFps_MapToNative()
    {
        var native = new FakeGstNative
        {
            PlayerCreateResult = new IntPtr(1),
            TimePos = 3.5,
            Duration = 12.0,
            Fps = 59.94,
        };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.GetProperty(ctx, "time-pos", api.FormatDouble, out double pos).Should().Be(0);
        pos.Should().Be(3.5);
        api.GetProperty(ctx, "duration", api.FormatDouble, out double dur).Should().Be(0);
        dur.Should().Be(12.0);
        api.GetProperty(ctx, "container-fps", api.FormatDouble, out double fps).Should().Be(0);
        fps.Should().Be(59.94);
        api.GetProperty(ctx, "mystery", api.FormatDouble, out _).Should().BeNegative();
    }

    [Fact]
    public void LoadFileCommand_PassesThroughCurrentPauseState()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();
        api.SetPropertyString(ctx, "pause", "yes");

        int rc = api.CommandString(ctx,
            MpvPlaybackCommandBuilder.BuildLoadFileCommand(@"D:\clip.mp4", 1.5));

        rc.Should().Be(0);
        native.LoadCalls.Should().Equal((@"D:/clip.mp4", (double?)1.5, true));
    }

    [Fact]
    public void RelativeSeek_IsResolvedAgainstCurrentPosition()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), TimePos = 8.0 };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.CommandString(ctx, "seek -2 relative+exact");

        native.SeekCalls.Should().Equal(6.0);
    }

    [Fact]
    public void GetPropertyString_PathWidthHeightCodec()
    {
        var native = new FakeGstNative
        {
            PlayerCreateResult = new IntPtr(1),
            Path = "a:/v.mp4",
            Width = 1920,
            Height = 1080,
            Decoder = "d3d11h264dec",
        };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.GetPropertyString(ctx, "path").Should().Be("a:/v.mp4");
        api.GetPropertyString(ctx, "width").Should().Be("1920");
        api.GetPropertyString(ctx, "height").Should().Be("1080");
        api.GetPropertyString(ctx, "video-codec").Should().Be("d3d11h264dec");
        api.GetPropertyString(ctx, "nothere").Should().BeEmpty();
    }
}

public class GstMpvRenderApiAdapterTests
{
    private static MpvRenderNative.MpvRenderParam[] BuildParams(
        IntPtr sizePtr, IntPtr stridePtr, IntPtr formatPtr, IntPtr pixelPtr)
    {
        return
        [
            new MpvRenderNative.MpvRenderParam { Type = 17, Data = sizePtr },
            new MpvRenderNative.MpvRenderParam { Type = 18, Data = formatPtr },
            new MpvRenderNative.MpvRenderParam { Type = 19, Data = stridePtr },
            new MpvRenderNative.MpvRenderParam { Type = 20, Data = pixelPtr },
            new MpvRenderNative.MpvRenderParam { Type = 0, Data = IntPtr.Zero },
        ];
    }

    [Fact]
    public void Render_ParsesSwParametersAndCopiesFrame()
    {
        var native = new FakeGstNative
        {
            PlayerCreateResult = new IntPtr(1),
            FrameWidth = 640,
            FrameHeight = 360,
        };
        var state = new GstBackendState(native);
        var api = new GstMpvRenderApiAdapter(state);
        IntPtr ctx = state.EnsurePlayer() ? state.Player : IntPtr.Zero;

        IntPtr sizeBuf = Marshal.AllocHGlobal(8);
        IntPtr strideBuf = Marshal.AllocHGlobal(8);
        IntPtr formatBuf = Marshal.StringToHGlobalAnsi("bgr0");
        IntPtr pixels = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(sizeBuf, 640);
            Marshal.WriteInt32(sizeBuf, 4, 360);
            Marshal.WriteInt64(strideBuf, 2560);

            int rc = api.RenderContextRender(ctx, BuildParams(sizeBuf, strideBuf, formatBuf, pixels));

            rc.Should().Be(0);
            native.LastCopyStride.Should().Be(2560);
            native.LastCopyDst.Should().Be(pixels);
            native.ReleaseBeforeAcquireOrder.Should().BeTrue();
        }
        finally
        {
            Marshal.FreeHGlobal(sizeBuf);
            Marshal.FreeHGlobal(strideBuf);
            Marshal.FreeHGlobal(formatBuf);
            Marshal.FreeHGlobal(pixels);
        }
    }

    [Fact]
    public void Render_SizeMismatch_ReturnsErrorWithoutCopy()
    {
        var native = new FakeGstNative
        {
            PlayerCreateResult = new IntPtr(1),
            FrameWidth = 1280,
            FrameHeight = 720,
        };
        var state = new GstBackendState(native);
        var api = new GstMpvRenderApiAdapter(state);
        state.EnsurePlayer();

        IntPtr sizeBuf = Marshal.AllocHGlobal(8);
        IntPtr strideBuf = Marshal.AllocHGlobal(8);
        IntPtr formatBuf = Marshal.StringToHGlobalAnsi("bgr0");
        try
        {
            Marshal.WriteInt32(sizeBuf, 640);
            Marshal.WriteInt32(sizeBuf, 4, 360);
            Marshal.WriteInt64(strideBuf, 2560);

            int rc = api.RenderContextRender(state.Player, BuildParams(sizeBuf, strideBuf, formatBuf, new IntPtr(99)));

            rc.Should().Be(-5);
            native.CpuCopyCalls.Should().Be(0);
        }
        finally
        {
            Marshal.FreeHGlobal(sizeBuf);
            Marshal.FreeHGlobal(strideBuf);
            Marshal.FreeHGlobal(formatBuf);
        }
    }

    [Fact]
    public void Render_NoFrame_ReturnsError()
    {
        var native = new FakeGstNative
        {
            PlayerCreateResult = new IntPtr(1),
            AcquireResult = false,
        };
        var state = new GstBackendState(native);
        var api = new GstMpvRenderApiAdapter(state);
        state.EnsurePlayer();

        IntPtr sizeBuf = Marshal.AllocHGlobal(8);
        IntPtr strideBuf = Marshal.AllocHGlobal(8);
        IntPtr formatBuf = Marshal.StringToHGlobalAnsi("bgr0");
        try
        {
            Marshal.WriteInt32(sizeBuf, 64);
            Marshal.WriteInt32(sizeBuf, 4, 64);
            Marshal.WriteInt64(strideBuf, 256);

            int rc = api.RenderContextRender(state.Player, BuildParams(sizeBuf, strideBuf, formatBuf, new IntPtr(7)));

            rc.Should().Be(-3);
        }
        finally
        {
            Marshal.FreeHGlobal(sizeBuf);
            Marshal.FreeHGlobal(strideBuf);
            Marshal.FreeHGlobal(formatBuf);
        }
    }

    [Fact]
    public void Update_ConsumesPendingFlagAsMpvFrameFlag()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), ConsumeUpdateResult = 1 };
        var state = new GstBackendState(native);
        var api = new GstMpvRenderApiAdapter(state);
        state.EnsurePlayer();

        api.RenderContextUpdate(state.Player).Should().Be(1ul);
        native.ConsumeUpdateCalls.Should().Be(1);
    }
}

public class GstBackendStateTests
{
    [Fact]
    public void LoadFile_ThroughApi_ReachesNativeLoad()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.CommandString(ctx, "no-osd seek 1.0 absolute+exact");

        native.SeekCalls.Should().HaveCount(1);
    }

    [Fact]
    public void SenderName_EnvOverrideIsHonored()
    {
        string name = "TCS-Test-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, name);
        try
        {
            GstBackendState.ResolveSenderName().Should().Be(name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, null);
        }
    }

    [Fact]
    public void SetExternalDevice_IsAdoptedOnPlayerCreate()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(0x11) };
        var state = new GstBackendState(native);
        var engineDevice = new IntPtr(0x1234_5678);

        state.SetExternalDevice(engineDevice);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        ctx.Should().Be(new IntPtr(0x11));
        native.PlayerCreateCalls.Should().Be(1);
        native.LastExternalDevice.Should().Be(engineDevice);
    }

    [Fact]
    public void DisposePlayer_IsIdempotentAndBlocksEnsure()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(5) };
        var state = new GstBackendState(native);
        var api = new GstMpvApiAdapter(state);
        IntPtr ctx = api.Create();

        api.TerminateDestroy(ctx);
        api.TerminateDestroy(ctx);

        state.Player.Should().Be(IntPtr.Zero);
        api.Create().Should().Be(IntPtr.Zero);
    }
}

/// <summary>IGstNativeApi の記録用 fake。ネイティブ DLL に依存しない。</summary>
file sealed class FakeGstNative : IGstNativeApi
{
    public IntPtr PlayerCreateResult { get; init; } = IntPtr.Zero;
    public string CreateError { get; init; } = "";
    public List<bool> SetPausedCalls { get; } = [];
    public List<(string Path, double? Start, bool Paused)> LoadCalls { get; } = [];
    public List<double> SeekCalls { get; } = [];
    public double TimePos { get; set; }
    public double Duration { get; set; } = 10;
    public double Fps { get; set; } = 30;
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Decoder { get; set; } = "";
    public bool AcquireResult { get; set; } = true;
    public int FrameWidth { get; set; } = 64;
    public int FrameHeight { get; set; } = 64;
    public bool SpoutReadyValue { get; set; } = true;
    public int ConsumeUpdateResult { get; set; }
    public int ConsumeUpdateCalls { get; private set; }
    public int CpuCopyCalls { get; private set; }
    public IntPtr LastCopyDst { get; private set; }
    public int LastCopyStride { get; private set; }

    private int _opSeq;
    private int _lastReleaseOp;
    private int _lastAcquireOp;

    public bool ReleaseBeforeAcquireOrder => _lastReleaseOp > 0 && _lastReleaseOp < _lastAcquireOp;

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
        error = "";
        LoadCalls.Add((path, startSeconds >= 0 ? startSeconds : null, paused));
        return 0;
    }

    public int Stop(IntPtr player) => 0;

    public int SetPaused(IntPtr player, bool paused)
    {
        SetPausedCalls.Add(paused);
        return 0;
    }

    public bool IsPaused(IntPtr player) => true;

    public ulong Seek(IntPtr player, double seconds)
    {
        SeekCalls.Add(seconds);
        return 1;
    }

    public ulong StepFrame(IntPtr player) => 1;
    public ulong GetGeneration(IntPtr player) => 7;
    public ulong SetGeneration(IntPtr player, ulong generation) => generation;
    public int SetSpeed(IntPtr player, double rate) => 0;
    public int SetVolume(IntPtr player, double volume0To100) => 0;
    public int SetMute(IntPtr player, bool mute) => 0;
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
    public void SetFrameCallback(IntPtr player, GstNative.TcsFrameNotifyDelegate? callback) { }
    public int ConsumeUpdate(IntPtr player) { ConsumeUpdateCalls++; return ConsumeUpdateResult; }

    public bool Acquire(IntPtr player, ulong generation, out GstNative.TcsFrameInfo info)
    {
        _lastAcquireOp = ++_opSeq;
        info = new GstNative.TcsFrameInfo
        {
            Generation = generation,
            Seq = 1,
            PtsNs = 0,
            Width = FrameWidth,
            Height = FrameHeight,
            IsGpu = 1,
            Slot = -1,
        };
        return AcquireResult;
    }

    public bool TryGetLeasedTexture(IntPtr player, out IntPtr texture, out uint subresource, out uint dxgiFormat)
    {
        texture = IntPtr.Zero;
        subresource = 0;
        dxgiFormat = 87;
        return false;
    }

    public int LeasedCpuCopy(IntPtr player, IntPtr dst, int dstStride)
    {
        CpuCopyCalls++;
        LastCopyDst = dst;
        LastCopyStride = dstStride;
        return 0;
    }

    public void Release(IntPtr player) => _lastReleaseOp = ++_opSeq;

    public int PublishSpoutVerification(IntPtr player) => 0;
    public int SendImage(IntPtr player, IntPtr bgra, int width, int height, int pitch) => 0;
    public string DecoderName(IntPtr player) => Decoder;
    public bool SpoutReady(IntPtr player) => SpoutReadyValue;

    public int DrainDeliveryEvents(IntPtr player, GstNative.TcsDeliveryEvent[] buffer, uint capacity, out uint count)
    {
        count = 0;
        return 0;
    }

    public int GetDeliveryStats(IntPtr player, out GstNative.TcsDeliveryStats stats)
    {
        stats = default;
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
}
