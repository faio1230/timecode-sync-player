using Xunit;
using FluentAssertions;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaybackControlsE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private readonly TimecodeSyncPlayerFixture _fx;

    public PlaybackControlsE2ETests(TimecodeSyncPlayerFixture fx)
    {
        _fx = fx;
    }

    [SkippableFact]
    public async Task PlayPause_TogglesButtonLabel()
    {
        SkipIfNeeded();

        await PauseIfPlaying();

        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "⏸", TimeSpan.FromSeconds(2));
        BtnLabel("BtnPlay").Should().Be("⏸");

        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "▶", TimeSpan.FromSeconds(2));
        BtnLabel("BtnPlay").Should().Be("▶");
    }

    private Window Win => _fx.MainWindow!;
    private Button Btn(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id)).AsButton();
    private string BtnLabel(string id) => Btn(id).Name;

    private void SkipIfNeeded()
    {
        if (_fx.Skipped)
            throw new SkipException(_fx.SkipReason ?? "前提条件不足");
    }

    private async Task PauseIfPlaying()
    {
        if (BtnLabel("BtnPlay") != "⏸")
            return;

        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "▶", TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
    }
}
