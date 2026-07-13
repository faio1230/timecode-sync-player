using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MainWindowResourceDisposerTests
{
    [Fact]
    public void DisposeAll_RunsActionsInOrder()
    {
        var calls = new List<string>();
        var disposer = new MainWindowResourceDisposer(
            disposeTimer: () => calls.Add("timer"),
            disposeRenderContext: () => calls.Add("render"),
            disposeMpv: () => calls.Add("mpv"),
            disposeLtc: () => calls.Add("ltc"),
            disposeSpout: () => calls.Add("spout"),
            disposeTimeline: () => calls.Add("timeline"),
            disposeBuffer: () => calls.Add("buffer"));

        disposer.DisposeAll();

        calls.Should().Equal("timer", "render", "mpv", "ltc", "spout", "timeline", "buffer");
    }
}
