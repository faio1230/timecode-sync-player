using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Contracts;

public interface IMpvApi
{
    IntPtr Create();
    int Initialize(IntPtr ctx);
    void TerminateDestroy(IntPtr ctx);
    int SetPropertyString(IntPtr ctx, string name, string value);
    int GetProperty(IntPtr ctx, string name, int format, out double result);
    string GetPropertyString(IntPtr ctx, string name);
    int CommandString(IntPtr ctx, string args);
    void Free(IntPtr data);
    /// <summary>
    /// T5: 非フラッシュのレート変更（世代を上げない）。既定は未対応（-1）。
    /// GStreamer 実装のみが shim の tcs_player_set_rate_instant を呼ぶ。
    /// 失敗（非対応・パイプライン拒否）は Smooth 使用不可として扱う。
    /// </summary>
    int SetRateInstant(IntPtr ctx, double rate) => -1;
    int FormatDouble { get; }
}
