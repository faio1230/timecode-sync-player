using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// シナリオを本番で回す前の事前確認。当たったら、その回は「無効（理由）」として数分以内に終える
/// （1 時間回してから試験側の条件の悪さに気づくのを防ぐ）。
/// 2026-09-24 に無効な回が続いた原因（重い処理と重なった、使う区間が真っ黒、計算した長さが負）を
/// ここで先に見る。
/// </summary>
internal static class ScenarioPreflight
{
    /// <summary>開始時の機械全体の CPU 使用率（%）がこれを超えたら無効。</summary>
    public const double MaxStartCpuPercent = 50.0;

    /// <summary>
    /// 同時に動いていたら無効にする重い処理（素材の解析・変換・圧縮）。MSBuild / VBCSCompiler は
    /// ビルド後もしばらく待機で残るので名前では見ない（動いているビルドは CPU 使用率で当たる）。
    /// </summary>
    public static readonly string[] HeavyProcessNames =
        { "ffmpeg", "ffprobe", "ffplay", "HandBrakeCLI", "7z", "7zG" };

    /// <summary>
    /// 重い処理が同時に動いていないかを見る。1 秒間の CPU 使用率（GetSystemTimes）と、既知の重い
    /// プロセスの有無。問題なければ null、あれば理由。
    /// </summary>
    public static string? CheckMachineIdle(double sampleSeconds = 1.0)
    {
        var heavy = new List<string>();
        foreach (string name in HeavyProcessNames)
        {
            Process[] found = Process.GetProcessesByName(name);
            if (found.Length > 0)
                heavy.Add($"{name}×{found.Length}");
            foreach (Process process in found)
                process.Dispose();
        }

        double cpu = SampleCpuPercent(sampleSeconds);
        var reasons = new List<string>();
        if (heavy.Count > 0)
            reasons.Add("重い処理が同時に動いている（" + string.Join(", ", heavy) + "）");
        if (double.IsFinite(cpu) && cpu > MaxStartCpuPercent)
            reasons.Add($"開始時の CPU 使用率 {cpu:F0}% > {MaxStartCpuPercent:F0}%");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    /// <summary>機械全体の CPU 使用率（%）。取れなければ NaN。</summary>
    public static double SampleCpuPercent(double sampleSeconds)
    {
        if (!GetSystemTimes(out long idle1, out long kernel1, out long user1))
            return double.NaN;
        Thread.Sleep(TimeSpan.FromSeconds(sampleSeconds));
        if (!GetSystemTimes(out long idle2, out long kernel2, out long user2))
            return double.NaN;
        long idle = idle2 - idle1;
        long total = (kernel2 - kernel1) + (user2 - user1); // kernel は idle を含む
        return total <= 0 ? double.NaN : 100.0 * (total - idle) / total;
    }

    /// <summary>
    /// 区間 [start, start+length] が [clipIn, clipOut] に収まり、長さが正か。問題なければ null。
    /// </summary>
    public static string? CheckRange(string what, double start, double length, double clipIn, double clipOut)
    {
        if (!double.IsFinite(start) || !double.IsFinite(length))
            return $"{what}: 値が数でない（start={start}, length={length}）";
        if (length <= 0)
            return $"{what}: 長さが正でない（{length:F3}s）";
        const double epsilon = 1e-6;
        if (start < clipIn - epsilon || start + length > clipOut + epsilon)
            return $"{what}: [{start:F3}, {start + length:F3}] がクリップ [{clipIn:F3}, {clipOut:F3}] に収まらない";
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
