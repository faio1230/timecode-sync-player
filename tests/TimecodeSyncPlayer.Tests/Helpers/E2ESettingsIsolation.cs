using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class E2ESettingsIsolation
{
    /// <summary>起動前に置く設定 JSON（省略時は空の設定で起動）。</summary>
    public const string SeedEnvironmentVariable = "TCS_E2E_SETTINGS_JSON";

    /// <summary>
    /// TCS_E2E_SETTINGS_JSON が指定されていれば、その各キーを baseJson に上書きして返す
    /// （未指定なら baseJson のまま）。テストが独自に settings.json を書く箇所でも、同じ口で
    /// 補正モードのような設定ファイル側の項目を差し込めるようにするためのもの。
    /// </summary>
    public static string SeedJson(string baseJson)
    {
        string? seed = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(seed))
            return baseJson;

        JsonObject merged = JsonNode.Parse(baseJson)?.AsObject() ?? [];
        if (JsonNode.Parse(seed) is JsonObject overrides)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in overrides)
                merged[entry.Key] = entry.Value?.DeepClone();
        }
        return merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string Configure(ProcessStartInfo startInfo)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "settings",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        // 既定は「空の設定で起動」。TCS_E2E_SETTINGS_JSON が指定されたときだけ、その内容を
        // 初期設定として置く（補正モードのような設定ファイル側の項目を測るための口）。
        // 指定しない実行の挙動は従来と完全に同じ。
        string? seed = Environment.GetEnvironmentVariable(SeedEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(seed))
            File.WriteAllText(settingsPath, seed);
        startInfo.Environment[AppSettingsManager.SettingsPathEnvironmentVariable] = settingsPath;
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
