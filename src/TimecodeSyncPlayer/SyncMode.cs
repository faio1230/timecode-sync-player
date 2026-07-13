namespace TimecodeSyncPlayer;

/// <summary>
/// LTC同期モード。
/// Single: 現在トラック単体に対して同期（従来動作）。
/// Continue: Playlist全体をTimelineとして連続同期。
/// </summary>
public enum SyncMode
{
    Single,
    Continue
}

/// <summary>
/// Gap領域での振る舞い。
/// Freeze: 直前トラックの最終フレームを保持。
/// Black: 黒フレームを表示。
/// </summary>
public enum GapBehavior
{
    Freeze,
    Black
}
