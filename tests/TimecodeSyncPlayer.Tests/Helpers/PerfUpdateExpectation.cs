namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// S-1: アプリログの "Playback perf" 区間（既定 2 秒）の frameUpdates 期待範囲。
/// 30fps 前提の固定値では 60fps 実素材で落ちるため、素材の fps から作る（2 秒 × fps ± 10%）。
/// </summary>
internal static class PerfUpdateExpectation
{
    /// <summary>
    /// 期待する frameUpdates の範囲。fps が不正なときは 30fps として扱う
    /// （プロジェクトに fps が無い場合の従来の既定と同じ）。
    /// </summary>
    public static (int Min, int Max) FrameUpdatesRange(double fps, double seconds)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            fps = 30.0;

        double expected = seconds * fps;
        // ±10% は 9/10・11/10 の整数比で計算する（0.9/1.1 の二進端数で 55 が 56 に切り上がる）。
        return ((int)Math.Floor(expected * 9.0 / 10.0), (int)Math.Ceiling(expected * 11.0 / 10.0));
    }
}
