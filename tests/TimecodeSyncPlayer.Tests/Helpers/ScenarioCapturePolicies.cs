namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// 参照採取（head/tail）の完了条件。位置が目標の 0〜+1 フレームに入ったら完了（必須）。
/// v0.6.4 2-4 (a): 一時停止中のシークは 1 枚（v0.6.3 段 5）で、最初のフレームは目標の 0〜+1 フレーム
/// （v0.6.3 設計書 12 節）なので、目標より前は「準備まだ」。以前は目標 ±1 フレームの対称だった。
/// 絵の変化は待ちを早く抜けるだけの条件で、前の採取と同じ絵でも位置が入れば採用する
/// （前トラック末尾の黒フェードアウト・次トラック冒頭の黒フェードインのような正当な同一絵）。
/// </summary>
internal static class ReferenceCaptureReadiness
{
    /// <summary>境界ちょうどを浮動小数の丸めで外さないための微小量。</summary>
    private const double BoundaryEpsilon = 1e-9;

    /// <summary>
    /// 下限は目標を TimeLabel の刻み（整数秒 + フレームの切り捨て）に落とした値。位置は TimeLabel から読むので、
    /// 目標が刻みの上に無いと、目標の後ろのフレームでも読みは目標の手前の刻みになる（29.97 の末尾の参照など）。
    /// </summary>
    public static bool IsReady(double observedPosition, double targetSeconds, double oneFrameSeconds) =>
        double.IsFinite(observedPosition) &&
        observedPosition >= OnTimeLabelGrid(targetSeconds, oneFrameSeconds) - BoundaryEpsilon &&
        observedPosition <= targetSeconds + oneFrameSeconds + BoundaryEpsilon;

    /// <summary>アプリの PlaybackTimeFormatter.FormatFrames と同じ切り捨てで、秒を表示の刻みに落とす。</summary>
    private static double OnTimeLabelGrid(double seconds, double oneFrameSeconds)
    {
        double fps = 1.0 / oneFrameSeconds;
        return Math.Floor(seconds) + (int)((seconds % 1.0) * fps) / fps;
    }
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
