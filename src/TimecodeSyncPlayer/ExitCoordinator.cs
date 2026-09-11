using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// 終了の状態機械と手順実行（段階 5.1）。ダイアログ表示中も再生・LTC・出力は止めず、
/// ShuttingDown で MainWindowResourceDisposer の手順を I8 の順序で 1 つずつ実行する。
/// 50ms 以上ブロックし得る手順は runOffUiThread で UI スレッドを止めない。時間上限は設けない。
/// </summary>
internal sealed class ExitCoordinator
{
    private readonly IExitDialogHost dialogs;
    private readonly MainWindowResourceDisposer cleanup;
    private readonly Func<Action, Task> runOffUiThread;
    private readonly Action forceExit;
    private readonly Action shutdownCompleted;
    private bool stagesRunning;

    public ExitCoordinator(
        IExitDialogHost dialogs,
        MainWindowResourceDisposer cleanup,
        Func<Action, Task> runOffUiThread,
        Action forceExit,
        Action shutdownCompleted)
    {
        this.dialogs = dialogs;
        this.cleanup = cleanup;
        this.runOffUiThread = runOffUiThread;
        this.forceExit = forceExit;
        this.shutdownCompleted = shutdownCompleted;
    }

    public ExitPhase Phase { get; private set; } = ExitPhase.Running;

    /// <summary>×／Alt+F4／Closing。e.Cancel に設定する値を返す。</summary>
    public bool OnClosingRequested()
    {
        ExitTransition transition = ExitTransitions.Decide(Phase, ExitInput.CloseRequested, cleanup.HasMoreStages);
        if (transition.Accepted && transition.NextPhase != Phase)
        {
            Phase = transition.NextPhase;
            if (Phase == ExitPhase.Confirming)
                dialogs.ShowConfirmation();
        }
        return transition.CancelClose;
    }

    /// <summary>キャンセル（既定ボタン・Enter・Esc）。</summary>
    public void CancelRequested()
    {
        ExitTransition transition = ExitTransitions.Decide(Phase, ExitInput.Cancel, cleanup.HasMoreStages);
        if (!transition.Accepted) return;
        Phase = transition.NextPhase;
        dialogs.CloseDialog();
    }

    /// <summary>通常終了。進捗表示へ切り替えて手順を開始する。</summary>
    public void NormalExitRequested()
    {
        ExitTransition transition = ExitTransitions.Decide(Phase, ExitInput.NormalExit, cleanup.HasMoreStages);
        if (!transition.Accepted) return;
        Phase = transition.NextPhase;
        dialogs.SwitchToProgress();
        if (stagesRunning) return;
        stagesRunning = true;
        _ = RunStagesAsync();
    }

    /// <summary>強制終了。追加確認なしで Forcing へ入る。</summary>
    public void ForceRequested()
    {
        ExitTransition transition = ExitTransitions.Decide(Phase, ExitInput.Force, cleanup.HasMoreStages);
        if (!transition.Accepted) return;
        Phase = transition.NextPhase;
        forceExit();
    }

    private async Task RunStagesAsync()
    {
        try
        {
            string? loggedStep = null;
            while (Phase == ExitPhase.ShuttingDown && cleanup.HasMoreStages)
            {
                ResourceCleanupStage stage = cleanup.PeekNextStage()!;
                if (!string.Equals(stage.StepName, loggedStep, StringComparison.Ordinal))
                {
                    loggedStep = stage.StepName;
                    Log.Information("終了手順: {Step}", stage.StepName);
                }
                dialogs.UpdateStep(stage.StepName);
                if (stage.RunsOffUiThread)
                    await runOffUiThread(cleanup.RunNextStage).ConfigureAwait(true);
                else
                    cleanup.RunNextStage();
            }
            if (Phase != ExitPhase.ShuttingDown) return;
            Phase = ExitPhase.Exited;
            foreach (Exception error in cleanup.Errors)
                Log.Error(error, "終了手順の失敗");
            dialogs.CloseDialog();
            shutdownCompleted();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "終了手順の実行に失敗");
        }
    }
}
