namespace TimecodeSyncPlayer;

/// <summary>
/// D39 K2: ObservableCollection の行を差し替えると、WPF の ListBox は選択を外す
/// （選択中のインスタンスが除去されて新しいインスタンスが入るため）。
/// 選択が外れた項目が「同じ Id の別インスタンスへ差し替えられた」ときだけ、
/// 同じ Id の行へ選び直すための判定をここに固定する。
/// </summary>
internal static class PlaylistSelectionRestore
{
    /// <summary>
    /// 差し替えで外れた項目を同じ Id の行へ戻すときのインデックス。戻さないときは null。
    /// - 同じ Id の行が見つからない（削除された）→ null
    /// - 同じインスタンスが残っている（利用者が空クリックで選択解除した）→ null
    /// </summary>
    public static int? IndexAfterReplacement(PlaylistState playlist, PlaylistTrack? removedTrack)
    {
        if (removedTrack is null)
            return null;

        PlaylistTrack? current = playlist.FindTrackById(removedTrack.Id);
        if (current is null || ReferenceEquals(current, removedTrack))
            return null;

        int index = playlist.FindIndexById(removedTrack.Id);
        return index >= 0 ? index : null;
    }
}
