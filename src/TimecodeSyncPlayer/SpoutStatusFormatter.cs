namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 R-11: 出力の欄に出す Spout の状態の文字（純粋関数）。ON のときだけ「Spout: 送信名」、OFF は空（表示しない）。
/// </summary>
internal static class SpoutStatusFormatter
{
    public const string Prefix = "Spout: ";

    public static string Format(bool enabled, string senderName) =>
        enabled ? Prefix + senderName : "";
}
