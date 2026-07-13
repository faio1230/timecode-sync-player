using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SpoutStartupStateTests
{
    [Fact]
    public void FromInitializationResult_EnablesButton_WhenSpoutInitialized()
    {
        SpoutStartupState state = SpoutStartupState.FromInitializationResult(true);

        state.IsButtonEnabled.Should().BeTrue();
        state.ToggleLabel.Should().BeNull();
    }

    [Fact]
    public void FromInitializationResult_DisablesButtonAndShowsUnavailableLabel_WhenSpoutFailed()
    {
        SpoutStartupState state = SpoutStartupState.FromInitializationResult(false);

        state.IsButtonEnabled.Should().BeFalse();
        state.ToggleLabel.Should().Be("Spout N/A");
    }
}
