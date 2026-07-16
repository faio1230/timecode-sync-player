using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class DisplaySelectionPolicyTests
{
    private static readonly DisplayTarget Primary = new(
        @"\\.\DISPLAY1",
        new DisplayBounds(0, 0, 1920, 1080),
        IsPrimary: true);

    private static readonly DisplayTarget Secondary = new(
        @"\\.\DISPLAY2",
        new DisplayBounds(1920, 0, 2560, 1440),
        IsPrimary: false);

    [Fact]
    public void Select_RestoresSavedDisplay()
    {
        DisplayTarget? selected = DisplaySelectionPolicy.Select(
            [Primary, Secondary],
            Secondary.DeviceName);

        selected.Should().Be(Secondary);
    }

    [Fact]
    public void Select_WhenSavedDisplayIsMissing_FallsBackToPrimary()
    {
        DisplayTarget? selected = DisplaySelectionPolicy.Select(
            [Secondary, Primary],
            @"\\.\DISPLAY9");

        selected.Should().Be(Primary);
    }

    [Fact]
    public void Select_WhenNoDisplayIsPrimary_FallsBackToFirst()
    {
        DisplayTarget? selected = DisplaySelectionPolicy.Select(
            [Secondary],
            savedDeviceName: null);

        selected.Should().Be(Secondary);
    }

    [Fact]
    public void Select_WhenNoDisplaysExist_ReturnsNull()
    {
        DisplaySelectionPolicy.Select([], Primary.DeviceName).Should().BeNull();
    }

    [Theory]
    [InlineData(true, @"\\.\DISPLAY1 (Primary)")]
    [InlineData(false, @"\\.\DISPLAY1")]
    public void DisplayName_IndicatesPrimaryDisplay(bool isPrimary, string expected)
    {
        var target = Primary with { IsPrimary = isPrimary };

        target.DisplayName.Should().Be(expected);
    }

    [Fact]
    public void ShouldClose_WhenTargetWithSameBoundsStillExists_ReturnsFalse()
    {
        DisplaySelectionPolicy.ShouldClose(Secondary, [Primary, Secondary]).Should().BeFalse();
    }

    [Fact]
    public void ShouldClose_WhenTargetIsDisconnected_ReturnsTrue()
    {
        DisplaySelectionPolicy.ShouldClose(Secondary, [Primary]).Should().BeTrue();
    }

    [Fact]
    public void ShouldClose_WhenTargetBoundsChange_ReturnsTrue()
    {
        DisplayTarget moved = Secondary with { Bounds = new DisplayBounds(0, 0, 1920, 1080) };

        DisplaySelectionPolicy.ShouldClose(Secondary, [Primary, moved]).Should().BeTrue();
    }
}
