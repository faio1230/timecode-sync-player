namespace TimecodeSyncPlayer.Contracts;

/// <summary>Spout 出力で共有する既定値と送信名の決め方（shim 側と OutputEngine 側の両方が参照する）。</summary>
public static class SpoutDefaults
{
    /// <summary>Spout 送信名の既定値。環境変数があればそちらが優先される。</summary>
    public const string DefaultSenderName = "TimecodeSyncPlayer";

    /// <summary>Spout 送信名を変える環境変数。</summary>
    public const string SenderNameEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    /// <summary>
    /// v0.6.6 R-11: 送信名を決める（純粋関数）。未設定・空・空白だけは既定名、それ以外は前後の空白を落とした値。
    /// 送信名を決める処理はこの関数 1 つだけにする（以前は 2 か所にあり、片方だけが Trim していた）。
    /// </summary>
    public static string ResolveSenderName(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultSenderName : configured.Trim();

    /// <summary>環境変数から送信名を決める。</summary>
    public static string SenderNameFromEnvironment() =>
        ResolveSenderName(Environment.GetEnvironmentVariable(SenderNameEnvironmentVariable));
}
