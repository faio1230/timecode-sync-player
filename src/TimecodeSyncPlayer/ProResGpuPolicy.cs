namespace TimecodeSyncPlayer;

/// <summary>ProRes の GPU 復号（v0.6.0）。既定は Auto（shim が NVIDIA のアダプタでだけ使う）。</summary>
internal enum ProResGpuMode
{
    Auto,
    On,
    Off,
}

/// <summary>
/// settings.json の proResGpu の解釈。未設定・未知の値は Auto（既定）として扱い、
/// 未知の値は警告を 1 回出す（変更には再起動が必要）。
/// </summary>
internal static class ProResGpuPolicy
{
    public const string AutoValue = "auto";
    public const string OnValue = "on";
    public const string OffValue = "off";

    /// <summary>UI の「起動時の値と違う」ときの表示。</summary>
    internal const string RestartNoticeText = "再起動の後に反映";

    public static ProResGpuMode Resolve(string? value, Action<string>? warnUnknown = null)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(AutoValue, StringComparison.OrdinalIgnoreCase))
        {
            return ProResGpuMode.Auto;
        }
        if (value.Equals(OnValue, StringComparison.OrdinalIgnoreCase))
            return ProResGpuMode.On;
        if (value.Equals(OffValue, StringComparison.OrdinalIgnoreCase))
            return ProResGpuMode.Off;

        warnUnknown?.Invoke(value);
        return ProResGpuMode.Auto;
    }

    internal static string Describe(ProResGpuMode mode) => mode switch
    {
        ProResGpuMode.On => OnValue,
        ProResGpuMode.Off => OffValue,
        _ => AutoValue,
    };

    /// <summary>選んだ値が起動時（shim に渡した値）と違うときだけ、再起動の案内を返す。</summary>
    internal static string RestartNotice(ProResGpuMode startup, ProResGpuMode selected) =>
        startup == selected ? string.Empty : RestartNoticeText;
}
