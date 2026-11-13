using System.IO;
using System.Runtime.CompilerServices;

namespace Erytheia.Tests.Helpers;

/// <summary>
/// v0.6.7: 試験のプロセスでは、アプリの設定（AppSettingsManager.Instance）を利用者の本物の場所でなく一時の場所に向ける。
/// MainWindow を DI で組む試験（MainWindowLifecycleTests・MainWindowGapFreezeRetryTests）は、窓を閉じるときに
/// Instance の設定を保存する。隔離が無いと %LOCALAPPDATA%\ERYTHEIA\settings.json（v0.6.6 までは
/// %LOCALAPPDATA%\TimecodeSyncPlayer\settings.json）を既定の値で書いていた。新しい場所に設定ができると、
/// 利用者の機体で v0.6.7 を初めて起動したときの旧の場所からの引き継ぎが走らない。
/// 試験のアセンブリが読まれたときに、ERYTHEIA_SETTINGS_PATH が無ければ一時の場所を付ける（付いていれば変えない）。
/// アプリを起動する E2E は起動するときに子のプロセスへ自分の設定の場所を渡す（E2ESettingsIsolation）。
/// </summary>
internal static class TestSettingsIsolation
{
    [ModuleInitializer]
    internal static void IsolateSettings()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppSettingsManager.SettingsPathEnvironmentVariable)))
            return;

        string directory = Path.Combine(Path.GetTempPath(), "Erytheia.Tests", "settings-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable(AppSettingsManager.SettingsPathEnvironmentVariable,
            Path.Combine(directory, "settings.json"));
        AppSettingsManager.ResetForTesting();
    }
}
