using System.Diagnostics;

namespace TimecodeSyncPlayer;

/// <summary>
/// D30: 誤デコードの単発 Jump をそのまま適用しないための妥当性ゲート。
/// 写像がギャップ／現在と別トラックになる Jump と、Fixed fps モードでデコーダ推定 fps が
/// 解決 fps と食い違う Jump は「未確認」とし、次の 1 フレームで値の連続（同値の Duplicate
/// または +1 フレーム）を確認できたときだけ適用する。同一トラック内の Jump は即時。
/// D31: 確認窓の時計はサンプル時計（FrameEndTimestamp の差 = ストリーム順）を優先し、
/// 値が無いフレーム（フレーム終端の QPC を持たない受信）だけ壁時計で判定する。
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

    /// <summary>
    /// v0.6.1 D2: 数える Duplicate か（保持の証拠・Jump の確認）。Duplicate で、Fixed fps モードでデコーダ推定 fps が解決 fps と
    /// 食い違わない（音が化けた最中の 1 枚を証拠にしない）。規則 4 の入口の数え方と Jump の確認で同じ述語を使う。
    /// 推定 fps が無い（null）なら疑わない。
    /// </summary>
    public static bool IsCountedDuplicate(
        TimecodeFrameDiagnosticStatus status, TimecodeFpsMode mode, double? detectedFps, double resolvedFps) =>
        status == TimecodeFrameDiagnosticStatus.Duplicate &&
        !(detectedFps is double detected && IsDetectedFpsSuspect(mode, detected, resolvedFps));

    /// <summary>
    /// 未確認 Jump の次フレームが値の連続を示すか（同値の Duplicate か +1 フレーム）。
    /// v0.6.1 D2: 同値の Duplicate は、数える Duplicate（<paramref name="countedDuplicate"/>、<see cref="IsCountedDuplicate"/>）の
    /// ときだけ確認になる（fps の疑わしい Duplicate では確定しない）。
    /// </summary>
    public static bool IsConfirmedBy(
        double pendingSeconds, double currentSeconds, double fps, TimecodeFrameDiagnosticStatus status,
        bool countedDuplicate = true)
    {
        if (status == TimecodeFrameDiagnosticStatus.Jump)
            return false;
        double frameSeconds = fps > 0 ? 1.0 / fps : 0.04;
        double delta = currentSeconds - pendingSeconds;
        if (status == TimecodeFrameDiagnosticStatus.Duplicate)
            return countedDuplicate && Math.Abs(delta) <= frameSeconds * 0.5;
        return delta >= frameSeconds * 0.5 && delta <= frameSeconds * 1.5;
    }

    /// <summary>
    /// D31: サンプル時計の差（ms）。両方の FrameEndTimestamp が有効なときだけ値を持ち、
    /// どちらかが 0 なら null（壁時計へフォールバックする）。
    /// </summary>
    public static double? SampleClockDifferenceMilliseconds(
        long pendingFrameEndTimestamp, long currentFrameEndTimestamp) =>
        pendingFrameEndTimestamp > 0 && currentFrameEndTimestamp > 0
            ? (currentFrameEndTimestamp - pendingFrameEndTimestamp) * 1000.0 / Stopwatch.Frequency
            : null;

    /// <summary>
    /// 未確認 Jump の確認は次の 1 フレームに限る。無信号を挟んだ古い保留は確認に使わない。
    /// D31: 判定はサンプル時計（FrameEndTimestamp = 同期ワード末尾の QPC、ストリーム順）の差で
    /// 行い、どちらかの値が 0 のときだけ壁時計（受信時刻）の差にする。
    /// </summary>
    public static bool IsWithinConfirmationWindow(
        long pendingFrameEndTimestamp, long currentFrameEndTimestamp,
        long pendingReceivedAt, long currentReceivedAt, double fps)
    {
        double frameMilliseconds = fps > 0 ? 1000.0 / fps : 40.0;
        double windowMilliseconds = Math.Clamp(frameMilliseconds * 2.5, 100.0, 500.0);
        double differenceMilliseconds = SampleClockDifferenceMilliseconds(
            pendingFrameEndTimestamp, currentFrameEndTimestamp)
            ?? currentReceivedAt - pendingReceivedAt;
        return differenceMilliseconds <= windowMilliseconds;
    }
}
