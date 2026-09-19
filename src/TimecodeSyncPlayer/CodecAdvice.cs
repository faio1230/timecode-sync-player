namespace TimecodeSyncPlayer;

/// <summary>素材のコーデックが現場準備ガイドの推奨に合っているか。</summary>
public enum CodecStanding
{
    /// <summary>判定できない（デコーダがまだ決まっていない、名前が分からない）。警告しない。</summary>
    Unknown,
    /// <summary>推奨（H.264）。</summary>
    Recommended,
    /// <summary>使える（ProRes）。警告しない。</summary>
    Acceptable,
    /// <summary>推奨外（VP9・AV1・H.265 など）。警告する。</summary>
    NotRecommended,
}

/// <summary>
/// 0.4.7: 読み込んだ素材のコーデックが推奨外なら警告する。判定は現場準備ガイドの推奨
/// （H.264 を推奨、ProRes は可）に合っているかだけで行い、「何 Mbps から危険」のような
/// 測定値のしきい値は使わない（利用者の方針。しきい値を支える実測が無い）。
///
/// 推奨外にしている根拠（検証機の実測、2026-09-19）:
/// VP9 4K60 は 2 秒単位の窓の約 18% で復号が追いつかず、同期が乱れる。
/// AV1 はキーフレーム間隔を読めず、キーフレーム間隔の警告が出せない。
/// ほかの形式（H.265 など）は測っていない。
///
/// デコーダ名は shim のプロファイル表（tcs_video_profiles.h）の名前がそのまま来る。
/// 判定できない名前は警告しない（誤って警告するより、警告しないほうが安全）。
/// </summary>
internal static class CodecAdvice
{
    internal static CodecStanding Classify(string? decoderName)
    {
        if (string.IsNullOrWhiteSpace(decoderName)) return CodecStanding.Unknown;
        string name = decoderName.Trim().ToLowerInvariant();
        if (name.Contains("h264")) return CodecStanding.Recommended;
        if (name.Contains("prores")) return CodecStanding.Acceptable;
        if (name.Contains("vp9") || name.Contains("av1") || name.Contains("dav1d")
            || name.Contains("h265") || name.Contains("hevc") || name.Contains("vp8")
            || name.Contains("mpeg2") || name.Contains("mpeg4"))
            return CodecStanding.NotRecommended;
        // decodebin(sysmem) は、専用の経路を持たない形式を汎用のデコーダで読んでいる状態。
        if (name.StartsWith("decodebin")) return CodecStanding.NotRecommended;
        return CodecStanding.Unknown;
    }

    /// <summary>表示に使う形式名。判定できないときは空。</summary>
    internal static string DisplayName(string? decoderName)
    {
        if (string.IsNullOrWhiteSpace(decoderName)) return string.Empty;
        string name = decoderName.Trim().ToLowerInvariant();
        if (name.Contains("h264")) return "H.264";
        if (name.Contains("prores")) return "ProRes";
        if (name.Contains("vp9")) return "VP9";
        if (name.Contains("av1") || name.Contains("dav1d")) return "AV1";
        if (name.Contains("h265") || name.Contains("hevc")) return "H.265";
        if (name.Contains("vp8")) return "VP8";
        if (name.Contains("mpeg2")) return "MPEG-2";
        if (name.Contains("mpeg4")) return "MPEG-4";
        if (name.StartsWith("decodebin")) return "その他の形式";
        return string.Empty;
    }

    /// <summary>ステータス行の文言。警告しないときは空。</summary>
    internal static string StatusText(string? decoderName)
    {
        if (Classify(decoderName) != CodecStanding.NotRecommended) return string.Empty;
        string display = DisplayName(decoderName);
        string codec = display.Length > 0 ? display : "この形式";
        string text = $"推奨外の形式（{codec}）です。同期が不安定になることがあります（推奨: H.264）";
        if (display == "AV1")
            text += "。キーフレーム間隔も確認できません";
        return text;
    }

    /// <summary>プレイリストの印。</summary>
    internal const string PlaylistMark = "⚠ 形式";

    /// <summary>プレイリストの印のツールチップ。</summary>
    internal const string PlaylistTooltip =
        "推奨外の形式です。同期が不安定になることがあります。H.264 で書き出し直すと安定します。";
}
