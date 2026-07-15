using System.Diagnostics;
using System.IO;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class E2ESettingsIsolation
{
    private const string SettingsPathEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SETTINGS_PATH";

    public static string Configure(ProcessStartInfo startInfo)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "settings",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        startInfo.Environment[SettingsPathEnvironmentVariable] = Path.Combine(directory, "settings.json");
        return directory;
    }

    public static void Delete(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // E2E後始末では、終了直後のファイルハンドル競合などによる例外を握り潰す。
        }
    }
}
