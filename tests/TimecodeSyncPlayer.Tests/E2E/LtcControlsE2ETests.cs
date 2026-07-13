using Xunit;
using FluentAssertions;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcControlsE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private readonly TimecodeSyncPlayerFixture _fx;

    public LtcControlsE2ETests(TimecodeSyncPlayerFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public void LtcControls_AreVisibleAndInitiallyReady()
    {
        SkipIfNeeded();

        Button refresh = Btn("BtnRefreshLtcDevices");
        refresh.IsEnabled.Should().BeTrue();

        Win.IsOffscreen.Should().BeFalse();
        Element("LtcDeviceCombo").Should().NotBeNull();
        Element("BtnStartLtc").Should().NotBeNull();
        Element("BtnStopLtc").Should().NotBeNull();
    }

    private Window Win => _fx.MainWindow!;
    private Button Btn(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id)).AsButton();
    private AutomationElement? Element(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private void SkipIfNeeded()
    {
        if (_fx.Skipped)
            throw new SkipException(_fx.SkipReason ?? "前提条件不足");
    }
}
