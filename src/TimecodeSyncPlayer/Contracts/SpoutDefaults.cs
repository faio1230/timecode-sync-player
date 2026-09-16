namespace TimecodeSyncPlayer.Contracts;

/// <summary>Spout 出力で共有する既定値（mpv 経路と GPU 経路の両方が参照する）。</summary>
public static class SpoutDefaults
{
    /// <summary>Spout 送信名の既定値。環境変数があればそちらが優先される。</summary>
    public const string DefaultSenderName = "TimecodeSyncPlayer";
}
