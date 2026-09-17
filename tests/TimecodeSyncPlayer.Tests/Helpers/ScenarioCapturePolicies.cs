namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// 参照採取（head/tail）の完了条件。位置が目標 ±1 フレームに入ったら完了（必須）。
/// 絵の変化は待ちを早く抜けるだけの条件で、前の採取と同じ絵でも位置が入れば採用する
/// （前トラック末尾の黒フェードアウト・次トラック冒頭の黒フェードインのような正当な同一絵）。
/// </summary>
internal static class ReferenceCaptureReadiness
{
    /// <summary>±1 フレームちょうどの境界を浮動小数の丸めで外さないための微小量。</summary>
    private const double BoundaryEpsilon = 1e-9;

    public static bool IsReady(double observedPosition, double targetSeconds, double oneFrameSeconds) =>
        double.IsFinite(observedPosition) &&
        Math.Abs(observedPosition - targetSeconds) <= oneFrameSeconds + BoundaryEpsilon;
}

/// <summary>
/// ジャンプ中の黒（jump-black）を数えてよいか。ジャンプ発行直前の絵が黒（黒率 ≥ 0.99）なら、
/// Held が黒のまま残っているだけでジャンプ中の黒ではないため数えない。
/// </summary>
internal static class JumpBlackPolicy
{
    public const double BlackFractionThreshold = 0.99;

    public static bool IsExempt(double beforeJumpBlackFraction) => beforeJumpBlackFraction >= BlackFractionThreshold;
}
