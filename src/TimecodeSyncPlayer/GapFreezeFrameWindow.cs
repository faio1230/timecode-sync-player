namespace TimecodeSyncPlayer;

/// <summary>
/// ギャップの Freeze で「目標のフレーム」とみなす窓（D21-b の ±2 フレーム）。
/// 到着の判定・確定の門（C-4）・遅延確定（D32）・合成層の保存（D26）が同じ窓を使う。
/// K3 f4-14: 位置はフレーム格子に乗った値なので、窓はフレーム数 × フレーム時間で表し、
/// 倍精度の誤差で境界ちょうどの値を弾かないよう 1 µs の余裕を足す。
/// </summary>
internal static class GapFreezeFrameWindow
{
    /// <summary>窓の幅（フレーム数、片側）。</summary>
    internal const double Frames = 2.0;

    // 秒の差の丸め誤差（ns 格子の値で 1e-9 未満）を吸収する余裕。フレーム時間より十分小さい。
    private const double EpsilonSeconds = 1e-6;

    /// <summary>1 フレームの秒。fps 不明（0 以下・非有限）は DefaultFallbackFps（30fps）として扱う。</summary>
    public static double FrameSeconds(double fps) =>
        1.0 / (double.IsFinite(fps) && fps > 0 ? fps : GapFreezeHandler.DefaultFallbackFps);

    /// <summary>位置が目標の ±2 フレーム（境界を含む）にあるか。</summary>
    public static bool Contains(double positionSeconds, double targetSeconds, double frameSeconds) =>
        Math.Abs(positionSeconds - targetSeconds) <= Frames * frameSeconds + EpsilonSeconds;
}
