using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackOperationsCoordinatorTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Commands = new();
        public readonly List<(string Name, string Value)> Properties = new();
        public bool IsMpvReady = true;
        public bool HasTimelinePanel = true;
        public int CommandResult;
        public Exception? CommandException;

        public PlaybackOperationsEffects Build() => new(
            IsMpvReady: () => { Calls.Add("IsMpvReady"); return IsMpvReady; },
            CommandString: command =>
            {
                Calls.Add($"CommandString({command})");
                Commands.Add(command);
                if (CommandException != null) throw CommandException;
                return CommandResult;
            },
            SetPropertyString: (name, value) =>
            {
                Calls.Add($"SetPropertyString({name},{value})");
                Properties.Add((name, value));
                return 0;
            },
            ResetPlayerStateForNewTrack: () => Calls.Add("ResetPlayerStateForNewTrack"),
            ResetVideoWidth: () => Calls.Add("ResetVideoWidth"),
            ResetVideoHeight: () => Calls.Add("ResetVideoHeight"),
            ClearLoadedTrackId: () => Calls.Add("ClearLoadedTrackId"),
            HasTimelinePanel: () => { Calls.Add("HasTimelinePanel"); return HasTimelinePanel; },
            ClearTimelineLoadedTrackId: () => Calls.Add("ClearTimelineLoadedTrackId"),
            SetSeekBarValueFromPlayer: value => Calls.Add($"SetSeekBarValueFromPlayer({value})"),
            SetTimeLabel: value => Calls.Add($"SetTimeLabel({value})"),
            SetPlayPauseIcon: value => Calls.Add($"SetPlayPauseIcon({value})"),
            ResetGapFreezeAll: () => Calls.Add("ResetGapFreezeAll"),
            ResetGapFreeze: () => Calls.Add("ResetGapFreeze"),
            ClearGapFreezeFrame: () => Calls.Add("ClearGapFreezeFrame"));
    }

    private static PlaybackOperationsCoordinator Create(Recorder recorder) =>
        new(new PlaybackControlState(), recorder.Build());

    [Fact]
    public void LoadFile_WithoutStart_OnSuccessUnpausesThenResetsStateInOrder()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFile("C:\\media\\clip.mp4");

        result.Should().BeTrue();
        recorder.Calls.Should().Equal(
            "IsMpvReady",
            "CommandString(no-osd loadfile \"C:/media/clip.mp4\" replace)",
            "SetPropertyString(pause,no)",
            "SetPlayPauseIcon(⏸)",
            "ResetPlayerStateForNewTrack",
            "ResetVideoWidth",
            "ResetVideoHeight",
            "ResetGapFreeze",
            "SetSeekBarValueFromPlayer(0)",
            "SetTimeLabel(0:00 / 0:00)");
    }

    [Fact]
    public void LoadFile_WithStart_OnSuccessDoesNotWritePauseProperty()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFile("C:\\media\\clip.mp4", 12.5);

        result.Should().BeTrue();
        recorder.Commands.Should().ContainSingle()
            .Which.Should().Be("no-osd loadfile \"C:/media/clip.mp4\" replace -1 start=12.500000");
        recorder.Properties.Should().BeEmpty();
        recorder.Calls.Should().ContainInOrder(
            "SetPlayPauseIcon(⏸)",
            "ResetPlayerStateForNewTrack",
            "ResetVideoWidth",
            "ResetVideoHeight",
            "ResetGapFreeze");
    }

    [Fact]
    public void LoadFilePaused_OnSuccessLoadsThenPausesWithoutPlayWrite()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFilePaused("C:\\media\\clip.mp4");

        result.Should().BeTrue();
        recorder.Commands.Should().Equal("no-osd loadfile \"C:/media/clip.mp4\" replace");
        recorder.Properties.Should().Equal(("pause", "yes"));
        recorder.Calls.Should().ContainInOrder(
            "CommandString(no-osd loadfile \"C:/media/clip.mp4\" replace)",
            "SetPropertyString(pause,yes)",
            "SetPlayPauseIcon(▶)",
            "ResetPlayerStateForNewTrack");
        recorder.Properties.Should().NotContain(("pause", "no"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(12.5, false)]
    public void LoadFile_OnCommandFailureReturnsFalseWithoutResettingState(
        double? startPosition,
        bool writesPauseProperty)
    {
        var recorder = new Recorder { CommandResult = -1 };
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFile("clip.mp4", startPosition);

        result.Should().BeFalse();
        recorder.Properties.Any().Should().Be(writesPauseProperty);
        recorder.Calls.Should().NotContain(call =>
            call.StartsWith("SetPlayPauseIcon", StringComparison.Ordinal) ||
            call == "ResetPlayerStateForNewTrack" ||
            call == "ResetGapFreeze");
    }

    [Fact]
    public void LoadFile_WhenMpvIsNotReadyReturnsFalseWithoutOtherEffects()
    {
        var recorder = new Recorder { IsMpvReady = false };
        var coordinator = Create(recorder);

        coordinator.LoadFile("clip.mp4").Should().BeFalse();

        recorder.Calls.Should().Equal("IsMpvReady");
    }

    [Theory]
    [InlineData(true, "no-osd seek 12.346 absolute+exact")]
    [InlineData(false, "seek 12.346 absolute+exact")]
    public void SeekTo_UsesSuppressOsdConditionAndInvariantThreeDecimalTarget(
        bool suppressOsd,
        string expectedCommand)
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.SeekTo(12.3456, suppressOsd).Should().BeTrue();

        recorder.Commands.Should().Equal(expectedCommand);
        recorder.Calls.Should().NotContain("IsMpvReady");
    }

    [Fact]
    public void SeekTo_WhenCommandFailsReturnsFalse()
    {
        var recorder = new Recorder { CommandResult = 1 };

        Create(recorder).SeekTo(1).Should().BeFalse();
    }

    [Fact]
    public void SeekTo_WhenCommandThrowsReturnsFalse()
    {
        var recorder = new Recorder { CommandException = new InvalidOperationException("test") };

        Create(recorder).SeekTo(1).Should().BeFalse();
    }

    [Fact]
    public void StopPlayback_ResetsPlaybackStateInOriginalOrder()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.StopPlayback();

        recorder.Calls.Should().Equal(
            "IsMpvReady",
            "CommandString(stop)",
            "SetPropertyString(pause,yes)",
            "SetPlayPauseIcon(▶)",
            "ResetPlayerStateForNewTrack",
            "ResetVideoWidth",
            "ResetVideoHeight",
            "ClearLoadedTrackId",
            "HasTimelinePanel",
            "ClearTimelineLoadedTrackId",
            "SetSeekBarValueFromPlayer(0)",
            "SetTimeLabel(0:00 / 0:00)",
            "SetPlayPauseIcon(▶)",
            "ResetGapFreezeAll",
            "ClearGapFreezeFrame");
    }

    [Fact]
    public void StopPlayback_WithoutTimelinePanelSkipsTimelineReset()
    {
        var recorder = new Recorder { HasTimelinePanel = false };

        Create(recorder).StopPlayback();

        recorder.Calls.Should().Contain("HasTimelinePanel");
        recorder.Calls.Should().NotContain("ClearTimelineLoadedTrackId");
    }

    [Fact]
    public void StopPlayback_WhenMpvIsNotReadyDoesNothingFurther()
    {
        var recorder = new Recorder { IsMpvReady = false };

        Create(recorder).StopPlayback();

        recorder.Calls.Should().Equal("IsMpvReady");
    }
}
