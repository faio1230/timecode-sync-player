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

    // ---- v0.5.4 段 B の準備: 着地の事象（世代の最初のフレームの配信）----

    [Fact]
    public void Seek_WithoutDelay_DeliversTheSeekGenerationImmediately()
    {
        ScenarioPlayback playback = Playing(2.0);
        ulong before = playback.CurrentGeneration;
        int landingsBefore = playback.LandingCount;
        var landed = new List<ulong>();
        playback.Landed += generation => landed.Add(generation);

        playback.Seek(9.0).Success.Should().BeTrue();

        playback.CurrentGeneration.Should().Be(before + 1);
        playback.DeliveredGeneration.Should().Be(playback.CurrentGeneration);
        playback.DeliveredSeconds.Should().Be(9.0);
        playback.LandingCount.Should().Be(landingsBefore + 1);
        landed.Should().Equal(playback.CurrentGeneration);
    }

    [Fact]
    public void Seek_WithLandingDelay_DeliversTheSeekGenerationAtLanding()
    {
        // (a) 一時停止中のシーク: ポンプが最初のフレームを配信して DeliveredGeneration が進む。
        ScenarioPlayback playback = Playing(2.0);
        ulong before = playback.CurrentGeneration;
        int landingsBefore = playback.LandingCount;
        var landed = new List<ulong>();
        playback.Landed += generation => landed.Add(generation);
        playback.SeekLandingDelaySeconds = 0.3;

        playback.Seek(10.0);
        playback.CurrentGeneration.Should().Be(before + 1, "シークで世代が進む");
        playback.DeliveredGeneration.Should().Be(before, "着地まではシーク前の世代のフレームのまま");
        playback.HasPendingSeek.Should().BeTrue();

        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));
        playback.DeliveredGeneration.Should().Be(before, "着地の遅れの間はまだ配信されない");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        playback.DeliveredGeneration.Should().Be(playback.CurrentGeneration,
            "着地でシーク世代の最初のフレームが配信される");
        playback.DeliveredSeconds.Should().BeApproximately(10.0, 1e-9);
        playback.LandingCount.Should().Be(landingsBefore + 1);
        landed.Should().Equal(before + 1);
    }

    [Fact]
    public void Load_WithLoadDuration_DeliversTheLoadGenerationWhenReady()
    {
        // (b) 開始位置つきのロード: ロードで世代が進み、最初のフレームで追いつく。
        var playback = new ScenarioPlayback(positionSeconds: 1, durationSeconds: 30, fps: 25);
        playback.LoadDurationSeconds = 0.5;
        int landingsBefore = playback.LandingCount;
        var landed = new List<ulong>();
        playback.Landed += generation => landed.Add(generation);

        playback.Load("clip.mp4", 3.0, paused: true).Success.Should().BeTrue();
        ulong loadGeneration = playback.CurrentGeneration;
        playback.DeliveredGeneration.Should().Be(0, "ロード中は最初のフレームがまだ無い");
        playback.IsLoading.Should().BeTrue();

        playback.AdvanceTime(TimeSpan.FromMilliseconds(300));
        playback.DeliveredGeneration.Should().Be(0, "ロード完了前は配信されない");
        playback.PositionSeconds.Should().Be(1.0, "ロード完了まで位置は前のまま");

        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));
        playback.IsLoading.Should().BeFalse();
        playback.DeliveredGeneration.Should().Be(loadGeneration,
            "最初のフレームでロード世代に追いつく");
        playback.DeliveredSeconds.Should().BeApproximately(3.0, 1e-9);
        playback.PositionSeconds.Should().Be(3.0);
        playback.Paused.Should().BeTrue();
        playback.LandingCount.Should().Be(landingsBefore + 1);
        landed.Should().Equal(loadGeneration);
    }

    [Fact]
    public void PausedSeek_DeliversExactlyOneFrameOfTheSeekGeneration()
    {
        // v0.6.3 段 5: 一時停止中のシークのポンプは新しい世代のフレームを 1 枚だけ配信し、
        // 配信した位置はその 1 枚のまま動かない（shim 側の主張は shim_test の
        // --paused-seek-one-frame。この偽の再生 API は同じ契約の模擬であることを固定する）。
        var playback = new ScenarioPlayback(positionSeconds: 2, durationSeconds: 30, fps: 25);
        playback.Load("clip.mp4", 2.0, paused: true);
        playback.SeekLandingDelaySeconds = 0.1;
        int landingsBefore = playback.LandingCount;

        playback.Seek(10.0);
        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        ulong seekGeneration = playback.CurrentGeneration;
        playback.DeliveredGeneration.Should().Be(seekGeneration);
        playback.DeliveredSeconds.Should().BeApproximately(10.0, 1e-9);

        playback.AdvanceTime(TimeSpan.FromMilliseconds(500));
        playback.DeliveredGeneration.Should().Be(seekGeneration);
        playback.DeliveredSeconds.Should().BeApproximately(10.0, 1e-9,
            "一時停止中は 2 枚目を配信しない（位置は 1 枚目の PTS のまま）");
        playback.LandingCount.Should().Be(landingsBefore + 1);
    }

    [Fact]
    public void ConsecutiveSeeks_OnlyTheLatestGenerationIsDelivered()
    {
        // (c) 連続したシーク（g の後に g+1）: 前のシークの着地は、新しい着地待ちの配信にならない。
        ScenarioPlayback playback = Playing(2.0);
        ulong deliveredBefore = playback.DeliveredGeneration;
        int landingsBefore = playback.LandingCount;
        playback.SeekLandingDelaySeconds = 0.3;

        playback.Seek(5.0);
        ulong firstSeekGeneration = playback.CurrentGeneration;
        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));

        playback.Seek(8.0);   // g+1。前の着地待ちを置き換える
        ulong secondSeekGeneration = playback.CurrentGeneration;
        secondSeekGeneration.Should().Be(firstSeekGeneration + 1);

        playback.AdvanceTime(TimeSpan.FromMilliseconds(200));   // 先のシークの着地時刻を過ぎる
        playback.DeliveredGeneration.Should().Be(deliveredBefore,
            "置き換えた前の着地待ち（g）のフレームは配信されない");
        playback.LandingCount.Should().Be(landingsBefore);

        playback.AdvanceTime(TimeSpan.FromMilliseconds(100));
        playback.DeliveredGeneration.Should().Be(secondSeekGeneration,
            "最後のシーク（g+1）の最初のフレームだけが着地になる");
        playback.DeliveredSeconds.Should().BeApproximately(8.0, 1e-9);
        playback.LandingCount.Should().Be(landingsBefore + 1);
    }
}
