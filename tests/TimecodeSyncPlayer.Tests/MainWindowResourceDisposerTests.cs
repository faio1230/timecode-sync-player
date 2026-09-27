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

    // ---- v0.5.4 終了時の間欠の切り分け: 段と処理の開始・終了の Debug 行 ----

    private static MainWindowResourceDisposer CreateLogged(List<string> log, Action? stopRender = null,
        Action? disposePlayer = null, Action? stopOutput = null) => new(
        () => { }, () => { }, disposePlayer ?? (() => { }), () => { }, () => { }, () => { }, () => { },
        stopRender: stopRender ?? (() => { }),
        closeFullscreen: () => { },
        stopOutput: stopOutput ?? (() => { }),
        disposeOutput: () => { },
        stopAcceptingNewWork: () => { },
        debugLog: log.Add);

    /// <summary>begin と end が同じ名前で入れ子に対になっていることを確かめ、閉じた名前を順に返す。</summary>
    private static List<string> AssertPaired(IEnumerable<string> log)
    {
        var open = new Stack<string>();
        var closed = new List<string>();
        foreach (string line in log)
        {
            string[] parts = line.Split(' ');
            string kind = parts[0];
            string key = parts[1];
            if (kind is "stage.begin" or "action.begin")
                open.Push(kind.Split('.')[0] + ":" + key);
            else if (kind is "stage.end" or "action.end")
            {
                open.Should().NotBeEmpty($"end の前に begin がある（{line}）");
                open.Pop().Should().Be(kind.Split('.')[0] + ":" + key, $"入れ子の対（{line}）");
                closed.Add(kind.Split('.')[0] + ":" + key);
            }
        }
        open.Should().BeEmpty("すべての begin に end がある");
        return closed;
    }

    [Fact]
    public void DebugLog_EveryStageAndActionHasPairedBeginAndEnd()
    {
        var log = new List<string>();
        var disposer = CreateLogged(log);

        disposer.DisposeAll();

        List<string> closed = AssertPaired(log);
        closed.Where(c => c.StartsWith("stage:")).Should().HaveCount(MainWindowResourceDisposerStageCount);
        closed.Where(c => c.StartsWith("action:")).Select(c => c["action:name=".Length..]).Should().Equal(
            "stopAcceptingNewWork", "stopRender", "stopOutput", "closeFullscreen", "disposeTimer",
            "disposeRenderContext", "disposePlayer", "disposeLtc", "disposeOutput", "disposeSpout",
            "disposeTimeline", "disposeBuffer");
        log.Should().OnlyContain(l => !l.StartsWith("action.skip"));
        log.Where(l => l.StartsWith("stage.end") || l.StartsWith("action.end"))
            .Should().OnlyContain(l => l.Contains(" elapsedMs="));
    }

    private const int MainWindowResourceDisposerStageCount = 9;

    [Fact]
    public void DebugLog_FailedActionStillWritesItsEnd_AndTheStageEnd()
    {
        var log = new List<string>();
        var disposer = CreateLogged(log, disposePlayer: () => throw new InvalidOperationException("player"));

        Assert.Throws<AggregateException>(disposer.DisposeAll);

        AssertPaired(log);
        log.Should().Contain(l => l.StartsWith("action.end name=disposePlayer ok=False"));
    }

    [Fact]
    public void DebugLog_SkippedPlayerDisposeIsWrittenWithTheReason()
    {
        var log = new List<string>();
        // 出力の停止に失敗すると、shim のプレイヤーは破棄しない（0.4.8）。ふだん出る破棄の行が無い理由を残す。
        var disposer = CreateLogged(log, stopOutput: () => throw new InvalidOperationException("output"));

        Assert.Throws<AggregateException>(disposer.DisposeAll);

        AssertPaired(log);
        log.Should().Contain("action.skip name=disposePlayer contextFreed=True outputStopped=False");
        log.Should().NotContain(l => l.StartsWith("action.begin name=disposePlayer"));
    }

    [Fact]
    public void DebugLog_StageBeginCarriesTheStepNameAndThread()
    {
        var log = new List<string>();
        var disposer = CreateLogged(log);

        disposer.RunNextStage();

        log[0].Should().StartWith("stage.begin index=0 step=" + MainWindowResourceDisposer.StopAcceptingStepName + " offUi=False thread=");
        log.Last().Should().StartWith("stage.end index=0 step=" + MainWindowResourceDisposer.StopAcceptingStepName + " elapsedMs=");
    }
}
