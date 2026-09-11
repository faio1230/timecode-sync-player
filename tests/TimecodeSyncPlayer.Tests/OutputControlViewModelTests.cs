using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.5: テストカードのトグルは TimelineOutputState／再生状態を変えない。</summary>
public class OutputControlViewModelTests
{
    private sealed class FakeController : IPlaybackController
    {
        public int ToggleCount;
        public int SeekCount;
        public int CycleCount;
        public void TogglePlayPause() => ToggleCount++;
        public void SeekRelative(double seconds) => SeekCount++;
        public void CycleSpeed() => CycleCount++;
    }

    [Fact]
    public void InitialState_IsOffAndLabelMatches()
    {
        var vm = new OutputControlViewModel();
        vm.InitializeTestCard(false);

        vm.TestCardEnabled.Should().BeFalse();
        vm.TestCardToggleLabel.Should().Be("Card: OFF");
    }

    [Fact]
    public void InitializeTestCard_AppliesStartupEnvironmentValue()
    {
        var vm = new OutputControlViewModel();
        vm.InitializeTestCard(true);

        vm.TestCardEnabled.Should().BeTrue();
        vm.TestCardToggleLabel.Should().Be("Card: ON");
    }

    [Fact]
    public void ToggleTestCard_ChangesOnlyCardState()
    {
        var controller = new FakeController();
        var player = new PlayerViewModel(controller);
        player.PlayPauseIcon = "⏸";
        player.TimeLabel = "1:23 / 4:56";
        player.SeekBarValue = 83;
        TimelineOutputState timeline = TimelineOutputState.Default;
        var vm = new OutputControlViewModel();
        vm.InitializeTestCard(false);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleTestCard();

        vm.TestCardEnabled.Should().BeTrue();
        vm.TestCardToggleLabel.Should().Be("Card: ON");
        changed.Should().Equal(
            nameof(OutputControlViewModel.TestCardEnabled),
            nameof(OutputControlViewModel.TestCardToggleLabel));
        controller.ToggleCount.Should().Be(0, "カード切替は再生コマンドを呼ばない");
        controller.SeekCount.Should().Be(0);
        controller.CycleCount.Should().Be(0);
        player.PlayPauseIcon.Should().Be("⏸");
        player.TimeLabel.Should().Be("1:23 / 4:56");
        player.SeekBarValue.Should().Be(83);
        timeline.Should().Be(TimelineOutputState.Default, "カード切替はタイムライン状態を変えない");
        timeline.TestCardEnabled.Should().BeFalse();
    }

    [Fact]
    public void ToggleTestCard_Twice_ReturnsToOff()
    {
        var vm = new OutputControlViewModel();
        vm.InitializeTestCard(false);

        vm.ToggleTestCard();
        vm.ToggleTestCard();

        vm.TestCardEnabled.Should().BeFalse();
        vm.TestCardToggleLabel.Should().Be("Card: OFF");
    }
}
