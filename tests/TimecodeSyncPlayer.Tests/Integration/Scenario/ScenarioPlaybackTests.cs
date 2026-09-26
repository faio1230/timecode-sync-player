using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C2: 偽の再生 API の設定（着地の遅れ・通り過ぎ・ロード・尺の到着・レート・失敗）を
/// 仮想時間で動かす（設計: docs/design/v0.5.4-scenario-layer.md §2-2）。
/// </summary>
public class ScenarioPlaybackTests
{
    private static ScenarioPlayback Playing(double position)
    {
        var playback = new ScenarioPlayback(positionSeconds: position, durationSeconds: 30, fps: 25);
        playback.Load("clip.mp4", position, paused: false);
        return playback;
    }

    [Fact]
    public void Seek_WithLandingDelay_KeepsPositionUntilLanding()
    {
        ScenarioPlayback playback = Playing(2.0);
        playback.SeekLandingDelaySeconds = 0.3;

        playback.Seek(10.0);
        playback.IsSeeking().Should().BeTrue();
        playback.PositionSeconds.Should().Be(2.0, "着地するまでは古い位置のまま");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));
        playback.PositionSeconds.Should().Be(2.0);
        playback.IsSeeking().Should().BeTrue();

        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        playback.PositionSeconds.Should().Be(10.0);
        playback.IsSeeking().Should().BeFalse();
    }

    [Fact]
    public void Seek_WithOvershoot_LandsPastTargetThenSettlesBack()
    {
        ScenarioPlayback playback = Playing(2.0);
        playback.SeekLandingDelaySeconds = 0.2;
        playback.SeekOvershootSeconds = 0.25;

        playback.Seek(10.0);
        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));
        playback.PositionSeconds.Should().BeApproximately(10.25, 1e-9, "着地は行き過ぎを含む");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        playback.PositionSeconds.Should().BeApproximately(10.1, 1e-9, "次の tick で目標へ戻ってから進む");
    }

    [Fact]
    public void Load_WithLoadDuration_IsSeekingUntilReadyAndStartsAtTheRequestedPosition()
    {
        var playback = new ScenarioPlayback(positionSeconds: 1, durationSeconds: 30, fps: 25);
        playback.LoadDurationSeconds = 0.5;

        playback.Load("clip.mp4", 3.0, paused: false).Success.Should().BeTrue();
        playback.IsSeeking().Should().BeTrue();
        playback.PositionSeconds.Should().Be(1.0, "ロード完了までは前の位置のまま");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(300));
        playback.IsSeeking().Should().BeTrue();

        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));
        playback.IsSeeking().Should().BeFalse();
        playback.PositionSeconds.Should().Be(3.0);
        playback.Paused.Should().BeFalse();
    }

    [Fact]
    public void DurationArrival_DelaysTryGetDurationUntilTheConfiguredTime()
    {
        var playback = new ScenarioPlayback(durationSeconds: 20, fps: 25);
        playback.DurationArrivalDelaySeconds = 0.4;

        playback.Load("clip.mp4", 0, paused: true);
        playback.TryGetDuration(out _).Should().BeFalse("到着前は尺が不明");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(300));
        playback.TryGetDuration(out _).Should().BeFalse();

        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        playback.TryGetDuration(out double duration).Should().BeTrue();
        duration.Should().Be(20.0);
    }

    [Fact]
    public void Rate_MovesPositionWhilePlaying()
    {
        ScenarioPlayback playback = Playing(0);

        playback.AdvanceTime(TimeSpan.FromSeconds(1));
        playback.PositionSeconds.Should().BeApproximately(1.0, 1e-9);

        playback.SetRateInstant(2.0).Success.Should().BeTrue();
        playback.AdvanceTime(TimeSpan.FromSeconds(1));
        playback.PositionSeconds.Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void FailureInjection_SeekLoadAndRateReturnFailedWithoutMovingState()
    {
        ScenarioPlayback playback = Playing(2.0);

        playback.SeekSucceeds = false;
        playback.Seek(9.0).Success.Should().BeFalse();
        playback.PositionSeconds.Should().Be(2.0, "失敗したシークは位置を変えない");

        playback.SeekSucceeds = true;
        playback.LoadSucceeds = false;
        playback.Load("other.mp4", 7.0, paused: false).Success.Should().BeFalse();
        playback.PositionSeconds.Should().Be(2.0, "失敗したロードは位置を変えない");

        playback.RateApplySucceeds = false;
        playback.SetRateInstant(1.5).Success.Should().BeFalse();
        playback.Rate.Should().Be(1.0, "拒否されたレートは適用しない");
    }

    [Fact]
    public void PositionGoesBackward_ReversesAdvance()
    {
        ScenarioPlayback playback = Playing(5.0);
        playback.PositionGoesBackward = true;

        playback.AdvanceTime(TimeSpan.FromSeconds(1));

        playback.PositionSeconds.Should().BeApproximately(4.0, 1e-9);
    }
}
