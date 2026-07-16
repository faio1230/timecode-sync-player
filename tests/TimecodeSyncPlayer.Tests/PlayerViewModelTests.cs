using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

public class PlayerViewModelTests
{
    private sealed class FakeController : IPlaybackController
    {
        public int ToggleCount;
        public double LastSeekSeconds;
        public int CycleCount;
        public void TogglePlayPause() => ToggleCount++;
        public void SeekRelative(double seconds) => LastSeekSeconds = seconds;
        public void CycleSpeed() => CycleCount++;
    }

    [Fact]
    public void PlayPauseCommand_CallsTogglePlayPause()
    {
        var ctrl = new FakeController();
        var vm = new PlayerViewModel(ctrl);

        vm.PlayPauseCommand.Execute(null);

        ctrl.ToggleCount.Should().Be(1);
    }

    [Fact]
    public void BackCommand_CallsSeekRelativeWithNegative()
    {
        var ctrl = new FakeController();
        var vm = new PlayerViewModel(ctrl);

        vm.BackCommand.Execute(null);

        ctrl.LastSeekSeconds.Should().BeNegative();
    }

    [Fact]
    public void FwdCommand_CallsSeekRelativeWithPositive()
    {
        var ctrl = new FakeController();
        var vm = new PlayerViewModel(ctrl);

        vm.FwdCommand.Execute(null);

        ctrl.LastSeekSeconds.Should().BePositive();
    }

    [Fact]
    public void SpeedCommand_CallsCycleSpeed()
    {
        var ctrl = new FakeController();
        var vm = new PlayerViewModel(ctrl);

        vm.SpeedCommand.Execute(null);

        ctrl.CycleCount.Should().Be(1);
    }

    [Fact]
    public void DisplayProperties_RaisePropertyChangedAndReturnAssignedValues()
    {
        var vm = new PlayerViewModel(new FakeController());
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.PlayPauseIcon = "⏸";
        vm.TimeLabel = "1:23 / 4:56";
        vm.MetaLine = "clip.mp4";
        vm.SeekBarValue = 83;
        vm.SeekBarMaximum = 296;
        vm.SpeedLabel = "2×";
        vm.MuteToggleLabel = "MUTE ON";
        vm.Volume = 37;

        vm.PlayPauseIcon.Should().Be("⏸");
        vm.TimeLabel.Should().Be("1:23 / 4:56");
        vm.MetaLine.Should().Be("clip.mp4");
        vm.SeekBarValue.Should().Be(83);
        vm.SeekBarMaximum.Should().Be(296);
        vm.SpeedLabel.Should().Be("2×");
        vm.MuteToggleLabel.Should().Be("MUTE ON");
        vm.Volume.Should().Be(37);
        changedProperties.Should().Equal(
            nameof(vm.PlayPauseIcon),
            nameof(vm.TimeLabel),
            nameof(vm.MetaLine),
            nameof(vm.SeekBarValue),
            nameof(vm.SeekBarMaximum),
            nameof(vm.SpeedLabel),
            nameof(vm.MuteToggleLabel),
            nameof(vm.Volume));
    }
}
