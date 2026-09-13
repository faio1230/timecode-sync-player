using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// C1(a) 計測用: ギャップ中のプレイヤーの扱い。
/// Pause は現状どおりプレイヤーを一時停止する。ComposeBlack はプレイヤーを止めず、
/// 合成側の黒だけでギャップを表現する。既定は Pause。
/// </summary>
internal enum GapPlayerMode
{
    Pause,
    ComposeBlack,
}

/// <summary>
/// 環境変数 TCS_GAP_MODE の解釈。未設定・未知の値は Pause（現状のまま）。
/// 未知の値の警告は Current 初期化時に 1 回だけ出す。
/// </summary>
internal static class GapPlayerModePolicy
{
    public const string EnvironmentVariable = "TCS_GAP_MODE";
    public const string PauseValue = "pause";
    public const string ComposeBlackValue = "compose-black";

    public static GapPlayerMode Current { get; } = Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        value => Log.Warning("TCS_GAP_MODE の未知の値 '{Value}' は pause として扱います", value));

    internal static GapPlayerMode Resolve(string? value, Action<string>? warnUnknown = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GapPlayerMode.Pause;
        if (value.Equals(ComposeBlackValue, StringComparison.OrdinalIgnoreCase))
            return GapPlayerMode.ComposeBlack;
        if (value.Equals(PauseValue, StringComparison.OrdinalIgnoreCase))
            return GapPlayerMode.Pause;
        warnUnknown?.Invoke(value);
        return GapPlayerMode.Pause;
    }

    internal static string Describe(GapPlayerMode mode) =>
        mode == GapPlayerMode.ComposeBlack ? ComposeBlackValue : PauseValue;
}
