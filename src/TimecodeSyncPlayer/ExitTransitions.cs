namespace TimecodeSyncPlayer;

/// <summary>終了の状態（段階 5.1）。Running → Confirming → ShuttingDown → Exited。Confirming／ShuttingDown から Forcing。</summary>
internal enum ExitPhase { Running, Confirming, ShuttingDown, Forcing, Exited }

internal enum ExitInput { CloseRequested, Cancel, NormalExit, Force, StepCompleted }

internal readonly record struct ExitTransition(ExitPhase NextPhase, bool Accepted, bool CancelClose);

/// <summary>
/// 終了状態機械の遷移表（段階 5.1）。UI に依存しない純粋関数として全セルを定義する。
/// "—"（その状態で選べない入力）と「無視」は Accepted=false・状態維持。
/// </summary>
internal static class ExitTransitions
{
    public static ExitTransition Decide(ExitPhase phase, ExitInput input, bool hasMoreSteps)
    {
        switch (input)
        {
            case ExitInput.CloseRequested:
                // ×／Alt+F4／Closing。Running だけ確認へ入り、閉じるのを止める。
                return phase switch
                {
                    ExitPhase.Running => new(ExitPhase.Confirming, Accepted: true, CancelClose: true),
                    ExitPhase.Confirming => new(ExitPhase.Confirming, false, true),
                    ExitPhase.ShuttingDown => new(ExitPhase.ShuttingDown, false, true),
                    ExitPhase.Forcing => new(ExitPhase.Forcing, false, true),
                    _ => new(ExitPhase.Exited, false, false),
                };
            case ExitInput.Cancel:
                // 既定ボタン・Enter・Esc。Confirming のみ Running へ戻す。
                return phase == ExitPhase.Confirming
                    ? new(ExitPhase.Running, true, false)
                    : new(phase, false, false);
            case ExitInput.NormalExit:
                return phase == ExitPhase.Confirming
                    ? new(ExitPhase.ShuttingDown, true, false)
                    : new(phase, false, false);
            case ExitInput.Force:
                return phase switch
                {
                    ExitPhase.Confirming or ExitPhase.ShuttingDown => new(ExitPhase.Forcing, true, false),
                    _ => new(phase, false, false),
                };
            case ExitInput.StepCompleted:
                if (phase != ExitPhase.ShuttingDown) return new(phase, false, false);
                return hasMoreSteps
                    ? new(ExitPhase.ShuttingDown, true, false)
                    : new(ExitPhase.Exited, true, false);
            default:
                return new(phase, false, false);
        }
    }
}
