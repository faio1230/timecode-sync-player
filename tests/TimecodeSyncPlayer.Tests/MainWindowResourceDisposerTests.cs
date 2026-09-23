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
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("player"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => calls.Add("buffer"),
            stopRender: () => calls.Add("stop"),
            closeFullscreen: () => { calls.Add("fullscreen"); throw failure; });

        Assert.Throws<AggregateException>(disposer.DisposeAll).InnerExceptions.Should().Equal(failure);
        disposer.DisposeAll();

        calls.Should().Equal("stop", "fullscreen", "timer", "render", "player", "ltc", "spout", "timeline", "buffer");
    }

    [Fact]
    public void DisposeAll_StopFailurePreservesRenderingDependencies()
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("stop");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("player"),
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
        var playerFailure = new InvalidOperationException("player");
        var ltcFailure = new InvalidOperationException("ltc");
        var disposer = new MainWindowResourceDisposer(
            () => { calls.Add("timer"); throw timerFailure; },
            () => calls.Add("render"),
            () => { calls.Add("player"); throw playerFailure; },
            () => { calls.Add("ltc"); throw ltcFailure; },
            () => calls.Add("spout"),
            () => calls.Add("timeline"),
            () => calls.Add("buffer"));

        var error = Assert.Throws<AggregateException>(disposer.DisposeAll);

        calls.Should().Equal("timer", "render", "player", "ltc", "spout", "timeline", "buffer");
        error.InnerExceptions.Should().Equal(timerFailure, playerFailure, ltcFailure);
    }

    [Fact]
    public void DisposeAll_ContextFailurePreservesPlayerAndBuffersButReleasesIndependentResources()
    {
        var calls = new List<string>();
        var contextFailure = new InvalidOperationException("render");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"),
            () => { calls.Add("render"); throw contextFailure; },
            () => calls.Add("player"),
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
            disposePlayer: () => calls.Add("player"),
            disposeLtc: () => calls.Add("ltc"),
            disposeSpout: () => calls.Add("spout"),
            disposeTimeline: () => calls.Add("timeline"),
            disposeBuffer: () => calls.Add("buffer"));

        disposer.DisposeAll();

        calls.Should().Equal("timer", "render", "player", "ltc", "spout", "timeline", "buffer");
    }

    [Fact]
    public void DisposeAll_StopsOutputAndReturnsLeasesBeforeDestroyingThePlayer()
    {
        // テスト 10: worker 停止（リース返却）→ shim 破棄 → 出力の解放、の順。
        var calls = new List<string>();
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("player"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => calls.Add("buffer"),
            stopRender: () => calls.Add("stop"),
            closeFullscreen: () => calls.Add("fullscreen"),
            stopOutput: () => calls.Add("outputStop"),
            disposeOutput: () => calls.Add("outputDispose"));

        disposer.DisposeAll();

        calls.Should().Equal("stop", "outputStop", "fullscreen", "timer", "render", "player", "ltc",
            "outputDispose", "spout", "timeline", "buffer");
    }

    [Fact]
    public void DisposeAll_OutputStopFailure_KeepsThePlayerAlive()
    {
        // 0.4.8: worker が止まったと確認できないうちは、リングを参照され得る shim を消さない。
        var calls = new List<string>();
        var failure = new InvalidOperationException("output stop");
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), () => calls.Add("render"), () => calls.Add("player"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => calls.Add("buffer"),
            stopRender: () => calls.Add("stop"),
            closeFullscreen: () => calls.Add("fullscreen"),
            stopOutput: () => { calls.Add("outputStop"); throw failure; },
            disposeOutput: () => calls.Add("outputDispose"));

        Assert.Throws<AggregateException>(disposer.DisposeAll).InnerExceptions.Should().Equal(failure);

        calls.Should().NotContain("player");
        calls.Should().NotContain("outputDispose");
    }
}
