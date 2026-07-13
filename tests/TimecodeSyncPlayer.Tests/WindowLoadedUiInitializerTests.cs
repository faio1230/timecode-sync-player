using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class WindowLoadedUiInitializerTests
{
    [Fact]
    public void Initialize_RunsStartupUiActionsInOrder()
    {
        var calls = new List<string>();
        var initializer = new WindowLoadedUiInitializer(
            bindPlaylist: () => calls.Add("playlist"),
            subscribeLtc: () => calls.Add("ltc"),
            refreshLtcDevices: () => calls.Add("refresh"),
            applyAutoOffset: () => calls.Add("auto-offset"));

        initializer.Initialize();

        calls.Should().Equal("playlist", "ltc", "refresh", "auto-offset");
    }
}
