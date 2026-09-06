using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MainWindowResourceDisposerTests
{
    [Fact]
    public void DisposeAll_FullscreenFailureDoesNotPreventCleanupOrRepeatAttempts()
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("fullscreen");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("mpv"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => calls.Add("buffer"),
            stopRender: () => calls.Add("stop"),
            closeFullscreen: () => { calls.Add("fullscreen"); throw failure; });

        Assert.Throws<AggregateException>(disposer.DisposeAll).InnerExceptions.Should().Equal(failure);
        disposer.DisposeAll();

        calls.Should().Equal("stop", "fullscreen", "timer", "render", "mpv", "ltc", "spout", "timeline", "buffer");
    }

    [Fact]
    public void DisposeAll_StopFailurePreservesRenderingDependencies()
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("stop");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("mpv"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => calls.Add("buffer"),
            stopRender: () => { calls.Add("stop"); throw failure; },
            closeFullscreen: () => calls.Add("fullscreen"));

        Assert.Throws<AggregateException>(disposer.DisposeAll).InnerExceptions.Should().Equal(failure);

        calls.Should().Equal("stop", "fullscreen", "timer", "ltc", "timeline");
    }

    [Fact]
    public void DisposeAll_CollectsFailuresAndStillReleasesIndependentResources()
    {
        var calls = new List<string>();
        var timerFailure = new InvalidOperationException("timer");
        var mpvFailure = new InvalidOperationException("mpv");
        var ltcFailure = new InvalidOperationException("ltc");
        var disposer = new MainWindowResourceDisposer(
            () => { calls.Add("timer"); throw timerFailure; },
            () => calls.Add("render"),
            () => { calls.Add("mpv"); throw mpvFailure; },
            () => { calls.Add("ltc"); throw ltcFailure; },
            () => calls.Add("spout"),
            () => calls.Add("timeline"),
            () => calls.Add("buffer"));

        var error = Assert.Throws<AggregateException>(disposer.DisposeAll);

        calls.Should().Equal("timer", "render", "mpv", "ltc", "spout", "timeline", "buffer");
        error.InnerExceptions.Should().Equal(timerFailure, mpvFailure, ltcFailure);
    }

    [Fact]
    public void DisposeAll_ContextFailurePreservesMpvAndBuffersButReleasesIndependentResources()
    {
        var calls = new List<string>();
        var contextFailure = new InvalidOperationException("render");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"),
            () => { calls.Add("render"); throw contextFailure; },
            () => calls.Add("mpv"),
            () => calls.Add("ltc"),
            () => calls.Add("spout"),
            () => calls.Add("timeline"),
            () => calls.Add("buffer"));

        var error = Assert.Throws<AggregateException>(disposer.DisposeAll);

        calls.Should().Equal("timer", "render", "ltc", "spout", "timeline");
        error.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(contextFailure);
    }

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
