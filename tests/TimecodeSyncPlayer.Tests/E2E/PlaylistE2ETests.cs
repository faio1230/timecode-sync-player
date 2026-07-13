using Xunit;
using FluentAssertions;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaylistE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private readonly TimecodeSyncPlayerFixture _fx;

    public PlaylistE2ETests(TimecodeSyncPlayerFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public void PlaylistControls_AreVisibleAndTrackCountIsShown()
    {
        SkipIfNeeded();

        Btn("BtnPreviousTrack").Should().NotBeNull();
        Btn("BtnNextTrack").Should().NotBeNull();
        CurrentTrackLabelText().Should().Contain("/2");
    }

    private Window Win => _fx.MainWindow!;
    private Button Btn(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id)).AsButton();

    private void SkipIfNeeded()
    {
        if (_fx.Skipped)
            throw new SkipException(_fx.SkipReason ?? "前提条件不足");
    }

    private string CurrentTrackLabelText()
    {
        var el = Win.FindFirstDescendant(cf => cf.ByAutomationId("CurrentTrackLabel"));
        if (el == null) return "";
        var tp = el.Patterns.Text.PatternOrDefault;
        if (tp != null)
        {
            string t = tp.DocumentRange.GetText(-1).Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return el.Name;
    }

}
