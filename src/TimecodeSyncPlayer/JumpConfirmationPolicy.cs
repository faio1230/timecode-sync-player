namespace TimecodeSyncPlayer;

/// <summary>
/// D30: 誤デコードの単発 Jump をそのまま適用しないための妥当性ゲート。
/// 写像がギャップ／現在と別トラックになる Jump と、Fixed fps モードでデコーダ推定 fps が
/// 解決 fps と食い違う Jump は「未確認」とし、次の 1 フレームで値の連続（同値の Duplicate
/// または +1 フレーム）を確認できたときだけ適用する。同一トラック内の Jump は即時。
/// </summary>
internal static class JumpConfirmationPolicy
{
    /// <summary>
    /// Fixed fps モードでデコーダ推定 fps が解決 fps と食い違うか。Auto は正規の fps 切替が
    /// あり得るため対象外。29.97 と 30 のような標準値内の差は食い違いにしない。
    /// </summary>
    public static bool IsDetectedFpsSuspect(TimecodeFpsMode mode, double detectedFps, double resolvedFps)
    {
        if (mode == TimecodeFpsMode.Auto)
            return false;
        if (!double.IsFinite(detectedFps) || !double.IsFinite(resolvedFps) || detectedFps <= 0 || resolvedFps <= 0)
            return false;
        return Math.Round(detectedFps) != Math.Round(resolvedFps);
    }

    /// <summary>未確認 Jump の次フレームが値の連続を示すか（同値の Duplicate か +1 フレーム）。</summary>
    public static bool IsConfirmedBy(
        double pendingSeconds, double currentSeconds, double fps, TimecodeFrameDiagnosticStatus status)
    {
        if (status == TimecodeFrameDiagnosticStatus.Jump)
            return false;
        double frameSeconds = fps > 0 ? 1.0 / fps : 0.04;
        double delta = currentSeconds - pendingSeconds;
        if (status == TimecodeFrameDiagnosticStatus.Duplicate)
            return Math.Abs(delta) <= frameSeconds * 0.5;
        return delta >= frameSeconds * 0.5 && delta <= frameSeconds * 1.5;
    }

    /// <summary>未確認 Jump の確認は次の 1 フレームに限る。無信号を挟んだ古い保留は確認に使わない。</summary>
    public static bool IsWithinConfirmationWindow(long pendingReceivedAt, long currentReceivedAt, double fps)
    {
        double frameMilliseconds = fps > 0 ? 1000.0 / fps : 40.0;
        double windowMilliseconds = Math.Clamp(frameMilliseconds * 2.5, 100.0, 500.0);
        return currentReceivedAt - pendingReceivedAt <= windowMilliseconds;
    }
}
