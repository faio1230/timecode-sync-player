using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SeekBarInteractionControllerTests
{
    [Fact]
    public void TrySetFromPointer_MapsPointerToSliderValue()
    {
        var controller = new SeekBarInteractionController();

        SeekBarPointerUpdate update = controller.TrySetFromPointer(
            pointerX: 75.0,
            width: 100.0,
            oldValue: 0.2,
            durationSeconds: 20.0);

        update.Applied.Should().BeTrue();
        update.SliderValue.Should().Be(0.75);
    }

    [Fact]
    public void TrySetFromPointer_IgnoresInvalidDuration()
    {
        var controller = new SeekBarInteractionController();

        SeekBarPointerUpdate update = controller.TrySetFromPointer(
            pointerX: 75.0,
            width: 100.0,
            oldValue: 0.2,
            durationSeconds: 0.0);

        update.Applied.Should().BeFalse();
        update.SliderValue.Should().Be(0.2);
    }

    [Fact]
    public void CreatePreview_ReturnsPositionOnlyWhileSeeking()
    {
        var controller = new SeekBarInteractionController();

        controller.CreatePreview(sliderValue: 0.5, durationSeconds: 20.0)
            .HasValue.Should().BeFalse();

        controller.BeginSeek();
        SeekBarPreview preview = controller.CreatePreview(sliderValue: 0.5, durationSeconds: 20.0);

        preview.HasValue.Should().BeTrue();
        preview.PositionSeconds.Should().Be(10.0);
    }

    [Fact]
    public void CreateCommit_ClampsSliderValueAndComputesTarget()
    {
        var controller = new SeekBarInteractionController();

        SeekBarCommit commit = controller.CreateCommit(
            sliderValue: 1.5,
            minimum: 0.0,
            maximum: 1.0,
            durationSeconds: 20.0);

        commit.ShouldCommit.Should().BeTrue();
        commit.SliderValue.Should().Be(1.0);
        commit.TargetSeconds.Should().Be(20.0);
    }

    [Fact]
    public void CreateCommit_IgnoresInvalidDuration()
    {
        var controller = new SeekBarInteractionController();

        SeekBarCommit commit = controller.CreateCommit(
            sliderValue: 0.5,
            minimum: 0.0,
            maximum: 1.0,
            durationSeconds: double.NaN);

        commit.ShouldCommit.Should().BeFalse();
    }

    [Fact]
    public void UpdatingFromPlayer_SuppressesValueChanged()
    {
        var controller = new SeekBarInteractionController();

        controller.BeginPlayerUpdate();

        controller.IsUpdatingFromPlayer.Should().BeTrue();
        controller.CreatePreview(sliderValue: 0.5, durationSeconds: 20.0)
            .HasValue.Should().BeFalse();
        controller.CreateCommit(sliderValue: 0.5, minimum: 0.0, maximum: 1.0, durationSeconds: 20.0)
            .ShouldCommit.Should().BeFalse();
    }
}
