namespace TimecodeSyncPlayer;

/// <summary>
/// キーフレーム間隔が長い素材の警告文言（ステータス行とプレイリスト行で共有）。
///
/// 判定そのものは読み込み時の静的スキャン（0.4.5-C3、<see cref="GopScanVerdict"/>）が行う。
/// 再生中の観測による旧判定（0.4.4/C2）は可変 GOP の実素材で誤検出したため v0.5.0 で削除した。
/// </summary>
internal static class LongGopWarningMessages
{
    public const string Recommendation =
        "キーフレーム間隔が長いため同期が不安定になることがあります（推奨: 1〜2 秒）";

    public static string Format(double medianIntervalSeconds) =>
        medianIntervalSeconds > 0
            ? FormattableString.Invariant($"{Recommendation}（キーフレーム間隔 約 {medianIntervalSeconds:F1} 秒）")
            : Recommendation;
}
