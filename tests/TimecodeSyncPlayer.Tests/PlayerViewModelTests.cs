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
}
