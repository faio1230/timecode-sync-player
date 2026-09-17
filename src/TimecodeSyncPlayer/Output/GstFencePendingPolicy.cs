namespace TimecodeSyncPlayer.Output;

/// <summary>D25-b: fence 未完了で見送ったリースの扱い。</summary>
internal enum GstFencePendingAction
{
    /// <summary>完了した。保留を解いて描画・公開する。</summary>
    Draw,

    /// <summary>未完了。Held を描き、リースを保持したまま次 tick で再確認する。</summary>
    Hold,

    /// <summary>連続 3 秒未完了。D28 と同じ扱い（新規処理を止め、資源は完了かデバイス消失まで保持）。</summary>
    Timeout,
}

/// <summary>
/// D25-b の純粋規則。tick ごとに「保留中のリースが描けるか」を決める。
/// 見送ったリースは shim へ返さず保持する（sequence を消費しない）ため、
/// 完了した tick でそのまま描画できる。上限は D28 と同じ連続 3 秒で、
/// 超過は恒久停止側（デバイス消失と同じ経路）に倒す。
/// </summary>
internal static class GstFencePendingPolicy
{
    public const double LimitSeconds = 3.0;

    public static GstFencePendingAction Decide(
        bool fenceComplete, bool hasPending, long pendingSinceQpc, long nowQpc, long frequency)
    {
        if (fenceComplete) return GstFencePendingAction.Draw;
        if (hasPending && frequency > 0 &&
            nowQpc - pendingSinceQpc >= (long)(frequency * LimitSeconds))
            return GstFencePendingAction.Timeout;
        return GstFencePendingAction.Hold;
    }
}
