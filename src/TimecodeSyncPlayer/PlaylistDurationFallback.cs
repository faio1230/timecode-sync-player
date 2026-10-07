using System.IO;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 F-7: クリップの長さの取り方の 2 番目。追加・開くときの軽い関数で長さが取れなかった行
/// （<see cref="PlaylistTrack.MediaDuration"/> が 0）に、読み込んだ素材の再生時の長さ
/// （<c>IPlaybackApi.TryGetDuration</c>）を書き戻す。それも無ければ 0 のまま（表示は不明）。
/// </summary>
/// <remarks>
/// 順: 軽い関数（追加・開く）→ 読み込んだときの再生時の長さ（ここ）→ 不明（0 のまま、表示は「--」）。
/// 書き戻すのは、読み込んでいる行のパスと再生中のパスが同じときだけ（読み込みの切り替えの途中で
/// 前の素材の長さを別の行に書かない）。既に長さのある行は触らない（軽い関数の値を上書きしない）。
/// 後ろの行の自動オフセットの置き直しは、その行の長さを読みに行った経路と同じにする
/// （<see cref="MarkUnavailable"/> で覚えた値。追加は「追加時に自動オフセット」の設定、
/// プロジェクトを開いたときは置き直さない）。覚えていない行は置き直さない（保存した位置を動かさない）。
/// 書き戻しは UI スレッドから今の <see cref="PlaylistState.UpdateMediaDuration"/> で行う。
/// </remarks>
internal sealed class PlaylistDurationFallback
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, bool> _unavailable = new();

    /// <summary>軽い関数で長さが取れなかった行と、その経路の置き直しの有無を覚える。</summary>
    public void MarkUnavailable(Guid trackId, bool recalculate)
    {
        lock (_gate)
            _unavailable[trackId] = recalculate;
    }

    public bool TryApplyFromLoadedMedia(
        PlaylistState playlist,
        Guid? loadedTrackId,
        Func<string?> getPlayerPath,
        double playerDurationSeconds)
    {
        if (!loadedTrackId.HasValue) return false;
        if (!(playerDurationSeconds > 0) || double.IsInfinity(playerDurationSeconds) || double.IsNaN(playerDurationSeconds))
            return false;
        PlaylistTrack? track = playlist.FindTrackById(loadedTrackId.Value);
        if (track is null || track.MediaDuration > TimeSpan.Zero) return false;
        // パスは長さの無い行のときだけ読む（毎 tick の P/Invoke を避ける）。
        string? playerPath = getPlayerPath();
        if (string.IsNullOrEmpty(playerPath) || !SamePath(track.FilePath, playerPath)) return false;

        bool recalculate;
        lock (_gate)
        {
            recalculate = _unavailable.TryGetValue(track.Id, out bool r) && r;
            _unavailable.Remove(track.Id);
        }
        Log.Information(
            "Media duration from the loaded clip: durationSec={Duration:F6} name={Name} recalculate={Recalculate} (the container probe gave none)",
            playerDurationSeconds, track.Name, recalculate);
        playlist.UpdateMediaDuration(track.Id, TimeSpan.FromSeconds(playerDurationSeconds), recalculate);
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
