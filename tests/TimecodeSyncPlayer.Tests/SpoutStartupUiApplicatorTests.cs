using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class SpoutStartupUiApplicatorTests
{
    [Fact]
    public void Apply_UpdatesButtonAndOptionalLabel()
    {
        bool? enabled = null;
        string? label = null;
        var applicator = new SpoutStartupUiApplicator(
            setButtonEnabled: value => enabled = value,
            setToggleLabel: value => label = value);

        applicator.Apply(new SpoutStartupState(false, "Spout N/A"));

        enabled.Should().BeFalse();
        label.Should().Be("Spout N/A");
    }

    [Fact]
    public void Apply_DoesNotOverwriteLabel_WhenStateHasNoLabel()
    {
        string? label = "unchanged";
        var applicator = new SpoutStartupUiApplicator(
            setButtonEnabled: _ => { },
            setToggleLabel: value => label = value);

        applicator.Apply(new SpoutStartupState(true, null));

        label.Should().Be("unchanged");
    }
}
