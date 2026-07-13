using Xunit;
using FluentAssertions;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class SyncModeE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private readonly TimecodeSyncPlayerFixture _fx;

    public SyncModeE2ETests(TimecodeSyncPlayerFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public void SyncModeControls_AreVisibleAndGapBehaviorStartsDisabled()
    {
        SkipIfNeeded();

        Element("SyncModeCombo").Should().NotBeNull();
        ComboBox gapBehaviorCombo = Win.FindFirstDescendant(cf => cf.ByAutomationId("GapBehaviorCombo")).AsComboBox();

        gapBehaviorCombo.Should().NotBeNull();
        gapBehaviorCombo.IsEnabled.Should().BeFalse("初期状態のSingleモードではGap Behaviorが無効化される");
    }

    private Window Win => _fx.MainWindow!;
    private AutomationElement? Element(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private void SkipIfNeeded()
    {
        if (_fx.Skipped)
            throw new SkipException(_fx.SkipReason ?? "前提条件不足");
    }
}
