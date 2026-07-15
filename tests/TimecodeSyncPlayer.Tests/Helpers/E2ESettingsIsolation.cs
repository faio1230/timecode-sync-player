using System.Diagnostics;
using System.IO;
using System.Threading;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class E2ESettingsIsolation
{
    public static string Configure(ProcessStartInfo startInfo)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "settings",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        startInfo.Environment[AppSettingsManager.SettingsPathEnvironmentVariable] =
            Path.Combine(directory, "settings.json");
        return directory;
    }

    public static void Delete(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch when (attempt < 4)
            {
                // 終了直後のファイルハンドル解放を短時間待って再試行する。
                Thread.Sleep(100);
            }
            catch
            {
                // E2E後始末では、権限差などによる最終的な削除失敗を握り潰す。
            }
        }
    }
}
