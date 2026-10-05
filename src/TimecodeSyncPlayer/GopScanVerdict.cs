namespace TimecodeSyncPlayer;

/// <summary>0.4.5-C3: 素材のシーク性能の判定。</summary>
public enum GopSeekQuality
{
    /// <summary>スキャンできていない（未実行・失敗・キーフレーム 0）。警告は出さない。</summary>
    Unknown,
    Ok,
    /// <summary>長いキーフレーム間隔の区間がある。</summary>
    Warning,
    /// <summary>シーク性能に問題が出る可能性が高い。</summary>
    Error,
}

/// <summary>
/// 0.4.5-C3: 読み込み時に測ったキーフレーム間隔から、シーク性能を判定する（純ロジック）。
///
/// 判定は<b>最大ギャップ</b>で行う。中央値では見逃すため: 実素材の測定で、中央値 0.708 秒
/// （推奨の 1〜2 秒に収まる）なのに最大 5.5 秒の区間を持つ素材があった。長いギャップの中へ
/// シークすると、その区間だけ実際に遅くなる（同一素材の実測: キーフレーム直後 244ms、
/// ギャップ中央 1,258ms、次のキーフレーム直前 2,164ms）。
///
/// 先頭ギャップ（0 → 最初のキーフレーム）と末尾ギャップ（最後のキーフレーム → 尺）も
/// 対象に含める。実素材で末尾ギャップが 3.9〜6.0 秒あり、冒頭・終端へのシークが遅くなる。
/// </summary>
internal static class GopScanVerdict
{
    /// <summary>この値を超える最大ギャップで警告。</summary>
    internal const double DefaultWarningSeconds = 5.0;

    /// <summary>この値を超える最大ギャップでエラー扱い（表示を強くする）。</summary>
    internal const double DefaultErrorSeconds = 10.0;

    internal static GopSeekQuality Judge(
        int keyframes,
        double maxGapSeconds,
        double warningSeconds = DefaultWarningSeconds,
        double errorSeconds = DefaultErrorSeconds)
    {
        // キーフレームが 2 枚未満なら判定しない。誤検出より無検出が安全。
        //
        // 1 枚しか取れないのは「本当に 1 枚」か「パーサがキーフレームを立てていない」かの
        // どちらかで、区別できない。実測（検証機）: AV1 3 本で 1 枚と出て、最大ギャップが
        // 尺そのもの（181〜251 秒）になった。ProRes・H.264・VP9 は ffprobe と完全一致。
        // この値はシークの行き先の見積もりにも使うので、誤ると数十秒先へ飛ぶ。
        if (keyframes < 2 || !double.IsFinite(maxGapSeconds) || maxGapSeconds <= 0)
            return GopSeekQuality.Unknown;
        if (maxGapSeconds > errorSeconds) return GopSeekQuality.Error;
        if (maxGapSeconds > warningSeconds) return GopSeekQuality.Warning;
        return GopSeekQuality.Ok;
    }

    /// <summary>ステータス行に出す 1 行。OK と Unknown は空（何も出さない）。</summary>
    internal static string Format(GopSeekQuality quality, double maxGapSeconds) => quality switch
    {
        GopSeekQuality.Error => FormattableString.Invariant(
            $"⚠ キーフレーム間隔が {maxGapSeconds:F1} 秒の区間があります。同期が不安定になる可能性があります（推奨 1〜2 秒）"),
        GopSeekQuality.Warning => FormattableString.Invariant(
            $"キーフレーム間隔が長い区間があります（最大 {maxGapSeconds:F1} 秒、推奨 1〜2 秒）"),
        _ => string.Empty,
    };
}
