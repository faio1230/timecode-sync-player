using System.IO;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 F-7: 起動時のログに ffprobe の有無を 1 行出すための確かめ（現場のログで分かるように）。
/// アプリは ffprobe を長さにも何にも使わない。PATH を探すだけで、起動はしない。
/// </summary>
internal static class FfprobePresence
{
    /// <summary>PATH 上の ffprobe.exe のフルパス。無ければ null。</summary>
    public static string? FindOnPath(string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;
        foreach (string entry in pathVariable.Split(Path.PathSeparator))
        {
            string dir = entry.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir, "ffprobe.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // PATH の壊れた項目は飛ばす。
            }
        }
        return null;
    }

    /// <summary>起動時のログの 1 行（Information）の本文。</summary>
    public static string Describe(string? found) => found is null
        ? "ffprobe: not found on PATH (not used; clip durations come from the media container)"
        : $"ffprobe: found on PATH at {found} (not used; clip durations come from the media container)";
}
