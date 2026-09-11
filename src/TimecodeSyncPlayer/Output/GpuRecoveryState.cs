namespace TimecodeSyncPlayer.Output;

internal enum GpuRecoveryPhase { Running, Lost, Recovering, Failed }

/// <summary>UI へ通知する復旧状態。</summary>
internal enum GpuOutputStatus { Recovering, Recovered, Failed }

/// <summary>
/// デバイス消失復旧の状態機械（段階 5.2）。自動復旧はプロセス寿命で 1 回だけ、
/// それ以降は BtnGpuRetry の手動再試行のみ。期限超過 fault（I9）はここへ入れない。
/// </summary>
internal sealed class GpuRecoveryState
{
    private readonly object gate = new();
    private GpuRecoveryPhase phase = GpuRecoveryPhase.Running;
    private bool autoRecoveryUsed;

    public GpuRecoveryPhase Phase { get { lock (gate) return phase; } }

    public bool AutoRecoveryUsed { get { lock (gate) return autoRecoveryUsed; } }

    /// <summary>
    /// GpuDeviceLostException（worker）。Running→Lost、Recovering→Failed、Lost／Failed は無視。
    /// </summary>
    public GpuRecoveryPhase OnDeviceLost()
    {
        lock (gate)
        {
            phase = phase switch
            {
                GpuRecoveryPhase.Running => GpuRecoveryPhase.Lost,
                GpuRecoveryPhase.Recovering => GpuRecoveryPhase.Failed,
                _ => phase,
            };
            return phase;
        }
    }

    /// <summary>
    /// Lost から自動復旧を開始する。初回だけ true。2 回目以降は Failed へ落として false。
    /// </summary>
    public bool TryAutoRecover()
    {
        lock (gate)
        {
            if (phase != GpuRecoveryPhase.Lost) return false;
            if (autoRecoveryUsed) { phase = GpuRecoveryPhase.Failed; return false; }
            autoRecoveryUsed = true;
            phase = GpuRecoveryPhase.Recovering;
            return true;
        }
    }

    public void OnRecovered()
    {
        lock (gate)
            if (phase == GpuRecoveryPhase.Recovering) phase = GpuRecoveryPhase.Running;
    }

    public void OnRecoveryFailed()
    {
        lock (gate)
            if (phase == GpuRecoveryPhase.Recovering) phase = GpuRecoveryPhase.Failed;
    }

    /// <summary>BtnGpuRetry。Failed からのみ Recovering へ。他は無効。</summary>
    public bool TryManualRetry()
    {
        lock (gate)
        {
            if (phase != GpuRecoveryPhase.Failed) return false;
            phase = GpuRecoveryPhase.Recovering;
            return true;
        }
    }
}
