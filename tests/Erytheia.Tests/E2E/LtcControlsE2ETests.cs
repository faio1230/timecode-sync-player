using Xunit;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using Erytheia.Tests.Helpers;

namespace Erytheia.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcControlsE2ETests : IClassFixture<ErytheiaFixture>
{
    private readonly ErytheiaFixture _fx;

    public LtcControlsE2ETests(ErytheiaFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public void LtcControls_AreVisibleAndInitiallyReady()
    {
        SkipIfNeeded();

        Button refresh = Btn("BtnRefreshLtcDevices");
        refresh.IsEnabled.Should().BeTrue();

        E2EAssert.WaitUntil(
            () => E2EWindowVisibility.IsVisible(Win),
            TimeSpan.FromSeconds(3));
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
