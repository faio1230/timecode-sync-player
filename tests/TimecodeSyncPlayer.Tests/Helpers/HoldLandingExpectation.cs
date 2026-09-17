namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// 保持（PlayHeld）の着地期待。ランスルーは信号（タイムコード）が止まっても動画が走り続けるため、
/// 期待位置は保持開始からの経過秒だけ動かす。停止モードは着地目標で固定。
/// </summary>
internal static class HoldLandingExpectation
{
    public static double ExpectedPosition(double mappedTarget, double elapsedSeconds, bool runThrough) =>
        runThrough ? mappedTarget + elapsedSeconds : mappedTarget;

    public static bool IsLanded(double observedPosition, double expectedPosition, double toleranceSeconds) =>
        double.IsFinite(observedPosition) &&
        Math.Abs(observedPosition - expectedPosition) <= toleranceSeconds;
}
