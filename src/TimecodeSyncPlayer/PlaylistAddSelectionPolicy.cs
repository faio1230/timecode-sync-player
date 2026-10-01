namespace TimecodeSyncPlayer;

/// <summary>
/// 「Playlist に追加」の完了で一覧の選択を再生中の行へ合わせ直すかを決める。
/// 追加は行を先に出してから長さを読むので、その間に利用者が選んだ行は上書きしない（K2）。
/// </summary>
internal static class PlaylistAddSelectionPolicy
{
    public static bool ShouldSyncSelection(int listSelectedIndex) => listSelectedIndex < 0;
}
