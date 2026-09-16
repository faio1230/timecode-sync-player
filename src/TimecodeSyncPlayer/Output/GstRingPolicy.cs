namespace TimecodeSyncPlayer.Output;

/// <summary>共有リングのリース選択（ステージ 6b）。</summary>
internal enum GstRingLeasePlan
{
    /// <summary>slot 0..2: リング Surface を使い、描画前にフェンス待ちを出す。</summary>
    UseRing,

    /// <summary>
    /// slot &lt; 0（旧サンプル経路）やリング未接続/範囲外: リースを返して NotReady。
    /// D8: shim の旧サンプル経路のテクスチャは shim デバイス上の非共有資源で、
    /// 合成デバイスでは描けないため、GPU 合成はリング外のリースを使わない。
    /// </summary>
    Reject,
}

/// <summary>
/// ステージ 6b の純粋規則。slot の用法とフェンス待ちの順序をテストで固定する。
/// shim の seq は単調増加で、共有フェンス値はその seq そのもの。
/// </summary>
internal static class GstRingPolicy
{
    public static GstRingLeasePlan Decide(int slot, int ringCount, bool ringOpen)
    {
        if (slot < 0) return GstRingLeasePlan.Reject;
        if (!ringOpen || slot >= ringCount) return GstRingLeasePlan.Reject;
        return GstRingLeasePlan.UseRing;
    }

    /// <summary>
    /// 新規のリング seq に対してだけ GPU キューの待ちを1回発行する。
    /// 同じ/古い seq では再度待たない（すでに過ぎたフェンス値は意味がない）。
    /// </summary>
    public static bool ShouldWaitFence(int slot, long sequence, long lastWaited)
        => slot >= 0 && sequence > lastWaited;
}
