namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// 保持（PlayHeld）の着地判定。ランスルーは信号（タイムコード）が止まっても動画が走り続けるため、
/// 着地は一点ではなく区間で見る: 動画は着地目標以上、かつ保持開始からの経過秒より先には進めない。
/// 区間の上限が経過秒で伸びるので、着地の所要が長い素材（長 GOP の実素材は 1〜3 秒）でも
/// 窓に入るサンプルが存在する。停止モードは着地目標 ± 許容の固定区間。
/// </summary>
internal static class HoldLandingExpectation
{
    public static (double Min, double Max) LandingRange(
        double mappedTarget, double elapsedSeconds, bool runThrough, double toleranceSeconds, double frameSeconds)
    {
        if (!runThrough)
            return (mappedTarget - toleranceSeconds, mappedTarget + toleranceSeconds);

        return (mappedTarget - toleranceSeconds, mappedTarget + elapsedSeconds + toleranceSeconds + frameSeconds);
    }

    public static bool IsLanded(double observedPosition, double rangeMin, double rangeMax) =>
        double.IsFinite(observedPosition) && observedPosition >= rangeMin && observedPosition <= rangeMax;

    /// <summary>着地後に「期待どおり進行しているか」を見るための基準（ランスルーは target + 経過秒）。</summary>
    public static double ExpectedPosition(double mappedTarget, double elapsedSeconds, bool runThrough) =>
        runThrough ? mappedTarget + elapsedSeconds : mappedTarget;
}
