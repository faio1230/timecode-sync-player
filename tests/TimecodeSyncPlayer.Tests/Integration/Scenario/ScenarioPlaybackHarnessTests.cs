using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C2: harness の再生が ScenarioPlayback に一本化され、ScenarioClock でだけ進むこと。
/// 旧 ctor（仮想時計なし）は従来どおり明示の AdvancePlayback でしか動かない。
/// </summary>
public class ScenarioPlaybackHarnessTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScenarioClockTick_AdvancesPlaybackThroughTheHarness()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.LoadCurrentFile();

        h.PlaybackSeconds.Should().Be(0.0, "読み込み直後は MediaIn");

        h.Tick100Milliseconds(5);   // 仮想時間で 500ms

        h.PlaybackSeconds.Should().BeApproximately(0.5, 1e-9);
        clock.MonotonicMilliseconds.Should().Be(50_500);
    }

    [Fact]
    public void LegacyHarness_TickDoesNotAdvancePlayback()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.LoadCurrentFile();
        h.AdvancePlayback(2.0);

        h.Tick100Milliseconds(5);

        h.PlaybackSeconds.Should().BeApproximately(2.0, 1e-9,
            "仮想時計のない旧経路は従来どおり明示の AdvancePlayback だけで動く");
    }

    [Fact]
    public void ScenarioPlaybackSeekDelay_IsObservableThroughTheHarness()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.LoadCurrentFile();
        h.Playback.SeekLandingDelaySeconds = 0.2;

        h.EndSeekBarInteraction(5.0);

        h.PlaybackSeconds.Should().Be(0.0, "着地するまでは位置が動かない");
        h.Playback.IsSeeking().Should().BeTrue();

        h.Tick100Milliseconds(2);   // 50_200 で着地

        h.PlaybackSeconds.Should().BeApproximately(5.0, 1e-9);
        h.Playback.IsSeeking().Should().BeFalse();

        h.Tick100Milliseconds();

        h.PlaybackSeconds.Should().BeApproximately(5.1, 1e-9, "着地後は再生が進む");
    }
}
