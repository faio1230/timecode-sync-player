using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcDeviceListRefreshPlannerTests
{
    [Fact]
    public void ResolveSelectedIndex_ReturnsPreviousSelectionIndex_WhenDeviceStillExists()
    {
        string[] devices = ["Input A", "Input B", "Input C"];

        LtcDeviceListRefreshPlanner.ResolveSelectedIndex("Input B", devices).Should().Be(1);
    }

    [Fact]
    public void ResolveSelectedIndex_ReturnsFirstIndex_WhenPreviousSelectionIsMissing()
    {
        string[] devices = ["Input A", "Input B"];

        LtcDeviceListRefreshPlanner.ResolveSelectedIndex("Missing", devices).Should().Be(0);
    }

    [Fact]
    public void ResolveSelectedIndex_ReturnsFirstIndex_WhenPreviousSelectionIsNull()
    {
        string[] devices = ["Input A", "Input B"];

        LtcDeviceListRefreshPlanner.ResolveSelectedIndex(null, devices).Should().Be(0);
    }

    [Fact]
    public void ResolveSelectedIndex_ReturnsMinusOne_WhenDeviceListIsEmpty()
    {
        LtcDeviceListRefreshPlanner.ResolveSelectedIndex("Input A", []).Should().Be(-1);
    }
}
