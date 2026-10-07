namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 R-12: プレイリストの行の見せ方（つかむ印とツールチップの文字）。見せ方だけで、
/// 並べ替えの動作（ドラッグの開始の判定・ドロップの位置・「上へ」「下へ」）には触らない。
/// </summary>
internal static class PlaylistRowLook
{
    /// <summary>行の左端に出すつかむ印。</summary>
    public const string GripText = "⋮⋮";

    /// <summary>
    /// 行とつかむ印のツールチップ。Continue でタイムラインの時間が重なるときは、
    /// <see cref="PlaylistState.FindTrackAtTimelinePosition"/> が上の行（番号の小さい行）から探して最初の行を使う。
    /// </summary>
    public const string RowToolTip =
        "ドラッグで並べ替える（「上へ」「下へ」でも動かせる）。Continue で時間が重なるときは上の行を再生する";
}
