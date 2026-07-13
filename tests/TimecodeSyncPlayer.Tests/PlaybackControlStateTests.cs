using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackControlStateTests
{
    [Fact]
    public void TogglePlayPause_InvertsPausedStateAndReturnsMpvValueAndIcon()
    {
        var state = new PlaybackControlState();

        PlaybackPauseChange change = state.TogglePlayPause();

        change.IsPaused.Should().BeFalse();
        change.MpvPauseValue.Should().Be("no");
        change.PlayPauseIcon.Should().Be("⏸");

        change = state.TogglePlayPause();

        change.IsPaused.Should().BeTrue();
        change.MpvPauseValue.Should().Be("yes");
        change.PlayPauseIcon.Should().Be("▶");
    }

    [Fact]
    public void SetPaused_UpdatesStateAndIcon()
    {
        var state = new PlaybackControlState();

        PlaybackPauseChange change = state.SetPaused(false);

        state.IsPaused.Should().BeFalse();
        change.MpvPauseValue.Should().Be("no");
        change.PlayPauseIcon.Should().Be("⏸");
    }

    [Fact]
    public void CycleSpeed_AdvancesThroughConfiguredSteps()
    {
        var state = new PlaybackControlState();

        PlaybackSpeedChange first = state.CycleSpeed();
        PlaybackSpeedChange second = state.CycleSpeed();
        PlaybackSpeedChange third = state.CycleSpeed();
        PlaybackSpeedChange fourth = state.CycleSpeed();

        first.Speed.Should().Be(2.0);
        first.Label.Should().Be("2×");
        second.Speed.Should().Be(4.0);
        third.Speed.Should().Be(0.5);
        fourth.Speed.Should().Be(1.0);
        fourth.Label.Should().Be("1×");
    }

    [Fact]
    public void ResetSpeed_RestoresDefaultSpeed()
    {
        var state = new PlaybackControlState();
        state.CycleSpeed();

        PlaybackSpeedChange change = state.ResetSpeed();

        change.Speed.Should().Be(1.0);
        change.Label.Should().Be("1×");
    }
}
