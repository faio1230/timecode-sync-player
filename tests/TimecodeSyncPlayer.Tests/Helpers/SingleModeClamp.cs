namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// Single の LTC → 素材位置の写像と終端の clamp。製品（D29）と同じく [MediaIn, MediaOut] で見る
/// （MediaOut 未設定は尺。呼び出し側で解決済み）。従来は [0, 尺] で、MediaOut が尺より短い
/// 実素材では終端の期待が尺になり過ぎていた。
/// </summary>
internal static class SingleModeClamp
{
    public static double Target(double ltcSeconds, double mediaInSeconds, double mediaOutSeconds) =>
        Math.Clamp(ltcSeconds, mediaInSeconds, mediaOutSeconds);

    /// <summary>
    /// 製品の境界ホールド（D33）と同じ ±2 フレーム（素材 fps 基準）の判定許容 + ε。
    /// 60fps 実素材の position=25.033（期待 25.000）を通す。
    /// </summary>
    public static double BoundaryHoldTolerance(double fps) =>
        2.0 / (fps > 0 ? fps : 30.0) + 1e-6;
}
