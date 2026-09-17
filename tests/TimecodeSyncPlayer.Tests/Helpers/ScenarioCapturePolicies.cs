namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// 参照採取（head/tail）の採取可否。シーク後に届いたフレームが前の採取と違い、
/// かつ位置が目標 ±1 フレームに入ったときだけ参照として固定する（D25 の古いフレーム対策）。
/// </summary>
internal static class ReferenceCaptureReadiness
{
    public static bool IsReady(
        FrameSignature candidate, FrameSignature? previous,
        double observedPosition, double targetSeconds, double oneFrameSeconds) =>
        (previous is not FrameSignature prev || !prev.IsSameFrameAs(candidate)) &&
        double.IsFinite(observedPosition) && Math.Abs(observedPosition - targetSeconds) <= oneFrameSeconds;
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
