using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFrameDisplayUpdaterTests
{
    [Fact]
    public void Update_UpdatesBitmapAndLogsFirstFrameOnlyOnce()
    {
        var calls = new List<string>();
        var updater = new RenderFrameDisplayUpdater(
            updateBitmap: (width, height) => calls.Add($"update:{width}x{height}"),
            logFirstFrame: (width, height) => calls.Add($"log:{width}x{height}"));

        double firstMs = updater.Update(320, 180);
        double secondMs = updater.Update(640, 360);

        firstMs.Should().BeGreaterThanOrEqualTo(0);
        secondMs.Should().BeGreaterThanOrEqualTo(0);
        calls.Should().Equal(
            "update:320x180",
            "log:320x180",
            "update:640x360");
    }

    [Fact]
    public void Reset_AllowsFirstFrameLogAgain()
    {
        var calls = new List<string>();
        var updater = new RenderFrameDisplayUpdater(
            updateBitmap: (width, height) => calls.Add($"update:{width}x{height}"),
            logFirstFrame: (width, height) => calls.Add($"log:{width}x{height}"));

        updater.Update(320, 180);
        updater.Reset();
        updater.Update(640, 360);

        calls.Should().Equal(
            "update:320x180",
            "log:320x180",
            "update:640x360",
            "log:640x360");
    }
}
