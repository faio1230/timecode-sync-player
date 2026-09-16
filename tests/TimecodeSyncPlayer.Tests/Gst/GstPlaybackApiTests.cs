using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

public class GstPlaybackApiTests
{
    private static (GstBackendState State, GstPlaybackApi Api, FakeGstNative Native) Create(bool createPlayer = true)
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        if (createPlayer) state.EnsurePlayer().Should().BeTrue();
        return (state, new GstPlaybackApi(state), native);
    }

    [Fact]
    public void Load_PassesStartAndPaused_AndMirrorsPause()
    {
        var (state, api, native) = Create();

        PlaybackResult result = api.Load("C:/media/clip.mp4", 12.5, paused: true);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        native.LoadCalls.Should().ContainSingle()
            .Which.Should().Be(("C:/media/clip.mp4", (double?)12.5, true));
        state.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void Load_WithoutStart_PassesNull()
    {
        var (_, api, native) = Create();

        api.Load("C:/a.mp4", null, false).Success.Should().BeTrue();

        native.LoadCalls.Should().ContainSingle().Which.Start.Should().BeNull();
    }

    [Fact]
    public void Load_EmptyPath_FailsWithoutNativeCall()
    {
        var (_, api, native) = Create();

        PlaybackResult result = api.Load("", null, false);

        result.Success.Should().BeFalse();
        native.LoadCalls.Should().BeEmpty();
    }

    [Fact]
    public void Load_NativeFailure_ReturnsErrorText()
    {
        var (_, api, native) = Create();
        native.LoadResult = -1;
        native.LoadError = "file not found";

        PlaybackResult result = api.Load("C:/missing.mp4", null, false);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("file not found");
    }

    [Fact]
    public void Load_WithoutPlayer_Fails()
    {
        var (_, api, native) = Create(createPlayer: false);

        api.Load("C:/a.mp4", null, false).Success.Should().BeFalse();
        native.LoadCalls.Should().BeEmpty();
    }

    [Fact]
    public void Seek_Success_IsSeekingUntilArrival()
    {
        var (_, api, native) = Create();
        native.DeliveryArrivals = 10;

        api.Seek(5.0).Success.Should().BeTrue();

        native.SeekCalls.Should().ContainSingle().Which.Should().Be(5.0);
        api.IsSeeking().Should().BeTrue();
        native.DeliveryArrivals = 11;
        api.IsSeeking().Should().BeFalse();
    }

    [Fact]
    public void Seek_Rejected_ReturnsFailureAndDoesNotMarkSeeking()
    {
        var (_, api, native) = Create();
        native.SeekResult = 0;

        PlaybackResult result = api.Seek(5.0);

        result.Success.Should().BeFalse();
        api.IsSeeking().Should().BeFalse();
    }

    [Fact]
    public void Seek_ClampsNegativeSecondsToZero()
    {
        var (_, api, native) = Create();

        api.Seek(-3).Success.Should().BeTrue();

        native.SeekCalls.Should().ContainSingle().Which.Should().Be(0.0);
    }

    [Fact]
    public void Seek_NonFinite_FailsWithoutNativeCall()
    {
        var (_, api, native) = Create();

        api.Seek(double.NaN).Success.Should().BeFalse();
        api.Seek(double.PositiveInfinity).Success.Should().BeFalse();

        native.SeekCalls.Should().BeEmpty();
    }

    [Fact]
    public void Load_ClearsSeekingState()
    {
        var (_, api, _) = Create();
        api.Seek(1.0).Success.Should().BeTrue();
        api.IsSeeking().Should().BeTrue();

        api.Load("C:/b.mp4", null, false).Success.Should().BeTrue();

        api.IsSeeking().Should().BeFalse();
    }

    [Fact]
    public void Stop_ClearsSeekingState()
    {
        var (_, api, _) = Create();
        api.Seek(1.0).Success.Should().BeTrue();

        api.Stop().Success.Should().BeTrue();

        api.IsSeeking().Should().BeFalse();
    }

    [Fact]
    public void SetPaused_MirrorsAndCallsNative()
    {
        var (state, api, native) = Create();

        api.SetPaused(false).Success.Should().BeTrue();

        state.IsPaused.Should().BeFalse();
        native.SetPausedCalls.Should().Equal(false);
    }

    [Fact]
    public void SetPaused_NativeFailure_ReturnsFailure()
    {
        var (_, api, native) = Create();
        native.SetPausedResult = -1;

        api.SetPaused(true).Success.Should().BeFalse();
    }

    [Fact]
    public void SetRate_NonPositive_FailsWithoutNativeCall()
    {
        var (_, api, native) = Create();

        api.SetRate(0).Success.Should().BeFalse();
        api.SetRate(-1).Success.Should().BeFalse();
        api.SetRate(double.NaN).Success.Should().BeFalse();
        native.SetSpeedCalls.Should().BeEmpty();

        api.SetRate(1.5).Success.Should().BeTrue();
        native.SetSpeedCalls.Should().Equal(1.5);
    }

    [Fact]
    public void SetRateInstant_ReturnsNativeResult()
    {
        var (_, api, native) = Create();
        native.SetRateInstantResult = 0;

        api.SetRateInstant(1.02).Success.Should().BeTrue();
        native.RateInstantCalls.Should().Equal(1.02);

        native.SetRateInstantResult = -1;
        api.SetRateInstant(1.02).Success.Should().BeFalse();
    }

    [Fact]
    public void SetVolumeAndMute_SwallowNativeFailure()
    {
        var (_, api, native) = Create();
        native.SetVolumeResult = -1;
        native.SetMuteResult = -1;

        api.SetVolume(40.5);
        api.SetMute(true);

        native.SetVolumeCalls.Should().Equal(40.5);
        native.SetMuteCalls.Should().Equal(true);
    }

    [Fact]
    public void Getters_PassThroughNativeValues()
    {
        var (_, api, native) = Create();
        native.TimePos = 3.5;
        native.Duration = 60;
        native.Fps = 59.94;
        native.Path = "C:/x.mp4";
        native.Width = 1920;
        native.Height = 1080;
        native.Decoder = "d3d11h264dec";

        api.TryGetTimePos(out double position).Should().BeTrue();
        position.Should().Be(3.5);
        api.TryGetDuration(out double duration).Should().BeTrue();
        duration.Should().Be(60);
        api.TryGetFps(out double fps).Should().BeTrue();
        fps.Should().Be(59.94);
        api.GetPath().Should().Be("C:/x.mp4");
        api.TryGetSize(out int width, out int height).Should().BeTrue();
        (width, height).Should().Be((1920, 1080));
        api.GetVideoCodec().Should().Be("d3d11h264dec");

        native.IsPausedValue = false;
        api.IsPaused().Should().BeFalse();
    }

    [Fact]
    public void Getters_WithoutPlayer_ReturnFalseOrEmpty()
    {
        var (_, api, _) = Create(createPlayer: false);

        api.TryGetTimePos(out _).Should().BeFalse();
        api.TryGetDuration(out _).Should().BeFalse();
        api.TryGetFps(out _).Should().BeFalse();
        api.TryGetSize(out _, out _).Should().BeFalse();
        api.GetPath().Should().BeEmpty();
        api.GetVideoCodec().Should().BeEmpty();
        api.IsPaused().Should().BeFalse();
        api.IsSeeking().Should().BeTrue("player 未作成は位置未確定としてシーク中と同じ扱い");
    }
}
