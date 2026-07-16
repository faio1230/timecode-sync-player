using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class AudioControlCoordinatorTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(37.5, 37.5)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void State_ClampsVolumeAndFormatsMuteLabel(double input, double expected)
    {
        var unmuted = new AudioControlState(isMuted: false, input);
        var muted = new AudioControlState(isMuted: true, input);

        unmuted.Volume.Should().Be(expected);
        unmuted.MuteToggleLabel.Should().Be("MUTE OFF");
        muted.Volume.Should().Be(expected);
        muted.MuteToggleLabel.Should().Be("MUTE ON");
    }

    [Fact]
    public void ApplyStartup_WritesBothPlayerPropertiesAndUpdatesUiWithoutPersisting()
    {
        var fixture = new AudioFixture(isMuted: true, volume: 42.5);

        fixture.Coordinator.ApplyStartup();

        fixture.PropertyWrites.Should().Equal(("mute", "yes"), ("volume", "42.5"));
        fixture.UiStates.Should().ContainSingle().Which.Should().Be(new AudioControlSnapshot(true, 42.5, "MUTE ON"));
        fixture.PersistedStates.Should().BeEmpty();
    }

    [Fact]
    public void ToggleMute_WritesMuteUpdatesLabelAndPersistsCurrentVolume()
    {
        var fixture = new AudioFixture(isMuted: false, volume: 73);

        fixture.Coordinator.ToggleMute();

        fixture.PropertyWrites.Should().ContainSingle().Which.Should().Be(("mute", "yes"));
        fixture.UiStates.Should().ContainSingle().Which.Should().Be(new AudioControlSnapshot(true, 73, "MUTE ON"));
        fixture.PersistedStates.Should().ContainSingle().Which.Should().Be(new AudioControlSnapshot(true, 73, "MUTE ON"));
    }

    [Fact]
    public void SetVolume_WhileMutedWritesVolumeAndPreservesMuteState()
    {
        var fixture = new AudioFixture(isMuted: true, volume: 100);

        fixture.Coordinator.SetVolume(25);

        fixture.PropertyWrites.Should().ContainSingle().Which.Should().Be(("volume", "25"));
        fixture.Coordinator.State.Should().Be(new AudioControlSnapshot(true, 25, "MUTE ON"));
        fixture.PersistedStates.Should().ContainSingle().Which.Should().Be(fixture.Coordinator.State);
    }

    [Fact]
    public void SetVolumeWhileMuted_ThenUnmuteRetainsSelectedVolume()
    {
        var fixture = new AudioFixture(isMuted: true, volume: 100);

        fixture.Coordinator.SetVolume(25);
        fixture.Coordinator.ToggleMute();

        fixture.PropertyWrites.Should().Equal(("volume", "25"), ("mute", "no"));
        fixture.Coordinator.State.Should().Be(new AudioControlSnapshot(false, 25, "MUTE OFF"));
        fixture.PersistedStates.Should().HaveCount(2);
    }

    [Fact]
    public void SetVolume_WhenClampedValueIsUnchangedDoesNotWriteOrPersist()
    {
        var fixture = new AudioFixture(isMuted: false, volume: 100);

        fixture.Coordinator.SetVolume(150);

        fixture.PropertyWrites.Should().BeEmpty();
        fixture.UiStates.Should().BeEmpty();
        fixture.PersistedStates.Should().BeEmpty();
    }

    private sealed class AudioFixture
    {
        public AudioFixture(bool isMuted, double volume)
        {
            Coordinator = new AudioControlCoordinator(
                new AudioControlState(isMuted, volume),
                new AudioControlEffects(
                    SetPropertyString: (name, value) =>
                    {
                        PropertyWrites.Add((name, value));
                        return 0;
                    },
                    ApplyUi: UiStates.Add,
                    Persist: PersistedStates.Add));
        }

        public AudioControlCoordinator Coordinator { get; }
        public List<(string Name, string Value)> PropertyWrites { get; } = [];
        public List<AudioControlSnapshot> UiStates { get; } = [];
        public List<AudioControlSnapshot> PersistedStates { get; } = [];
    }
}
