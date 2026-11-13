using System.IO;
using System.Runtime.CompilerServices;

namespace Erytheia.Tests.Helpers;

/// <summary>
/// v0.6.7: 試験のプロセスでは、アプリの設定（AppSettingsManager.Instance）を利用者の本物の場所でなく一時の場所に向ける。
/// App.ConfigureServices で MainWindow の部品を組む試験（MainWindowGapFreezeRetryTests・MainWindowLifecycleTests など）は
/// Instance を使い、LoadAsync を呼ばないまま UpdateAsync で保存する。隔離が無いと本物の
/// %LOCALAPPDATA%\ERYTHEIA\settings.json（v0.6.6 までは %LOCALAPPDATA%\TimecodeSyncPlayer\settings.json）を既定の値で
/// 上書きしていた。新しい場所に設定ができると、利用者の機体で v0.6.7 を初めて起動したときの引き継ぎが走らない。
/// 試験のアセンブリが読まれたとき（どの試験のコードよりも先）に、ERYTHEIA_SETTINGS_PATH を一時の場所にする。
/// 既に付いていても、本物の新旧の場所の下を指していれば一時の場所に替える。旧の名前の変数は外す。
/// 上書きのパスがあると Instance は旧の場所からの引き継ぎもしない（AppSettingsManager.LegacyFilePath が null）。
/// アプリを起動する E2E は、起動するときに子のプロセスへ自分の設定の場所を渡す（E2ESettingsIsolation）。
/// </summary>
internal static class TestSettingsIsolation
{
    private const string LegacySettingsPathEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SETTINGS_PATH";

    /// <summary>試験が触ってはいけない本物の設定のフォルダ（新旧）。</summary>
    internal static IReadOnlyList<string> RealSettingsDirectories { get; } = new[]
    {
        AppSettingsManager.AppDataDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppSettingsManager.LegacyAppDataDirectoryName),
    };

    [ModuleInitializer]
    internal static void IsolateSettings()
    {
        Environment.SetEnvironmentVariable(LegacySettingsPathEnvironmentVariable, null);
        string? current = Environment.GetEnvironmentVariable(AppSettingsManager.SettingsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(current) || IsUnderRealSettingsDirectory(current))
        {
            string directory = Path.Combine(Path.GetTempPath(), "Erytheia.Tests", "settings-" + Environment.ProcessId);
            Environment.SetEnvironmentVariable(AppSettingsManager.SettingsPathEnvironmentVariable,
                Path.Combine(directory, "settings.json"));
        }
        AppSettingsManager.ResetForTesting();
    }

    /// <summary>パスが本物の新旧の設定のフォルダの下か（Windows に合わせて大文字小文字を区別しない）。</summary>
    internal static bool IsUnderRealSettingsDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        return RealSettingsDirectories.Any(directory =>
            full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}
