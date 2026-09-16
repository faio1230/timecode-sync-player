using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ExitProgressTests
{
    [Fact]
    public void ProgressStepNames_MatchI8Order()
    {
        MainWindowResourceDisposer.StepNames.Should().Equal(
            "新規受付停止",
            "GStreamer 停止",
            "出力停止（Spout 完了待ち）",
            "全画面終了",
            "資源解放");
    }

    [Fact]
    public void DisposerStages_ExposeProgressNamesInI8Order()
    {
        var names = new List<string>();
        MainWindowResourceDisposer disposer = CreateDisposer();
        while (disposer.HasMoreStages)
        {
            names.Add(disposer.PeekNextStage()!.StepName);
            disposer.RunNextStage();
        }

        names.Distinct().Should().Equal(MainWindowResourceDisposer.StepNames);
        names[0].Should().Be("新規受付停止");
        names[^1].Should().Be("資源解放");
    }

    [Fact]
    public void BlockingStages_RunOffUiThread_UiOnlyStagesDoNot()
    {
        var stages = new List<ResourceCleanupStage>();
        MainWindowResourceDisposer disposer = CreateDisposer();
        while (disposer.HasMoreStages)
        {
            stages.Add(disposer.PeekNextStage()!);
            disposer.RunNextStage();
        }

        stages[0].StepName.Should().Be(MainWindowResourceDisposer.StopAcceptingStepName);
        stages[0].RunsOffUiThread.Should().BeFalse();
        // RenderSession.Stop と OutputEngine.Stop は 50ms 以上ブロックし得る。
        stages[1].StepName.Should().Be(MainWindowResourceDisposer.StopPlaybackStepName);
        stages[1].RunsOffUiThread.Should().BeTrue();
        stages[2].StepName.Should().Be(MainWindowResourceDisposer.StopOutputStepName);
        stages[2].RunsOffUiThread.Should().BeTrue();
        // 全画面ウィンドウとタイマーは UI スレッド専用。
        stages[3].StepName.Should().Be(MainWindowResourceDisposer.CloseFullscreenStepName);
        stages[3].RunsOffUiThread.Should().BeFalse();
        // 資源解放には FreeContext・OutputEngine.Dispose・Spout join が含まれる。
        stages.Skip(4).Any(s => s.RunsOffUiThread).Should().BeTrue();
    }

    private static MainWindowResourceDisposer CreateDisposer() => new(
        disposeTimer: () => { },
        disposeRenderContext: () => { },
        disposePlayer: () => { },
        disposeLtc: () => { },
        disposeSpout: () => { },
        disposeTimeline: () => { },
        disposeBuffer: () => { },
        stopRender: () => { },
        stopOutput: () => { },
        disposeOutput: () => { },
        closeFullscreen: () => { },
        stopAcceptingNewWork: () => { });
}
