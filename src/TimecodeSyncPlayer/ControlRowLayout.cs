namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6: 再生コントロールの行の幅の配分（純粋関数）。中央の再生の並び（|◀ ▶| の右の ◀◀ -1F ⏸ +1F ▶▶）を
/// 切らないため、右側の並び（MUTE・音量・速度・Spout・Card・Timeline）の最大の幅を、行の幅から左の並びと
/// 中央の並びの幅を引いた残りにする。右側は折り返しのパネルなので、残りに収まらなければ下の行へ折り返す。
/// </summary>
internal static class ControlRowLayout
{
    /// <summary>
    /// 右側の並びに使える最大の幅。行の幅が分からない（0 以下・NaN）ときは制限しない（+∞）。
    /// 残りが <paramref name="minimumRightWidth"/> より狭いときは、その幅を返す（右側の 1 つの部品は必ず並べる）。
    /// </summary>
    public static double RightGroupMaxWidth(double rowWidth, double leftWidth, double centerWidth, double minimumRightWidth)
    {
        if (double.IsNaN(rowWidth) || rowWidth <= 0)
            return double.PositiveInfinity;
        double remaining = rowWidth - Math.Max(0, leftWidth) - Math.Max(0, centerWidth);
        return Math.Max(Math.Max(0, minimumRightWidth), remaining);
    }
}
