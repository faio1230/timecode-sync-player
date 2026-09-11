namespace TimecodeSyncPlayer;

/// <summary>
/// キャンバスサイズ変更の可否（段階 4.3、純粋関数）。
/// 再生中・LTC 追従中・Freeze／準備待ちのいずれかなら変更不可。
/// LTC 同期 ON の信号ロス中も追従再開があり得るため不可（呼び出し側が SyncEnabled を渡す）。
/// </summary>
internal static class CanvasChangeGate
{
    public static bool CanChange(bool isPlaying, bool isLtcFollowing, bool isRenderingFrozenOnly)
        => !isPlaying && !isLtcFollowing && !isRenderingFrozenOnly;

    /// <summary>不可の理由（可なら null）。UI のツールチップに出す。</summary>
    public static string? DescribeReason(bool isPlaying, bool isLtcFollowing, bool isRenderingFrozenOnly)
    {
        if (isPlaying) return "再生中はキャンバスを変更できません。";
        if (isLtcFollowing) return "LTC 同期が有効なためキャンバスを変更できません（信号ロス中も不可）。";
        if (isRenderingFrozenOnly) return "Freeze／準備待ちで画が止まっているためキャンバスを変更できません。";
        return null;
    }
}
