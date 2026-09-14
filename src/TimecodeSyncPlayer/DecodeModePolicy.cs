namespace TimecodeSyncPlayer;

/// <summary>デコード方式。既定は Hardware（現行と同じ）。Software は CPU デコーダを先に試す。</summary>
internal enum DecodeMode
{
    Hardware,
    Software,
}

/// <summary>
/// settings.json の decodeMode の解釈。未設定・未知の値は Hardware（既定）として扱い、
/// 未知の値は警告を 1 回出す（UI は設けず、変更には再起動が必要）。
/// </summary>
internal static class DecodeModePolicy
{
    public const string HardwareValue = "hardware";
    public const string SoftwareValue = "software";

    public static DecodeMode Resolve(string? value, Action<string>? warnUnknown = null)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals(HardwareValue, StringComparison.OrdinalIgnoreCase))
        {
            return DecodeMode.Hardware;
        }
        if (value.Equals(SoftwareValue, StringComparison.OrdinalIgnoreCase))
            return DecodeMode.Software;

        warnUnknown?.Invoke(value);
        return DecodeMode.Hardware;
    }

    internal static string Describe(DecodeMode mode) =>
        mode == DecodeMode.Software ? SoftwareValue : HardwareValue;
}
