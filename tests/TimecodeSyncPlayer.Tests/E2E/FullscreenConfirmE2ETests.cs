using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 R-8: 主画面（作業中の画面）に全画面を出すときの確認。
///  (1) 主画面を選んで FULLSCREEN → 確認が出る → 「やめる」で全画面にならない → もう一度押して「出す」で全画面 → Esc で戻る
///  (2) 主画面でない出力先では確認が出ない（2 枚目の画面が無ければスキップ）
/// 設定は一時の場所に切り離し、出力先は設定の fullscreenDisplayDeviceName で決める。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class FullscreenConfirmE2ETests
{
    [SkippableFact(Timeout = 120_000)]
    public void PrimaryDisplay_ConfirmCancelThenShow_EscapeReturns()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        DisplayTarget? primary = new NativeDisplayCatalog().GetDisplays().FirstOrDefault(d => d.IsPrimary);
        Skip.If(primary == null, "主画面が見つからない");

        string directory = NewTempDir("fullscreen-confirm-primary");
        try
        {
            string settingsPath = WriteSettings(directory, primary!.DeviceName);
            using var app = E2EAppRunner.Start(exePath, "--vo null", settingsPath);
            Window main = app.MainWindow;
            E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").IsEnabled, TimeSpan.FromSeconds(10));

            // 1 回目: 確認が出る。「やめる」で全画面にならない。
            app.Button("BtnFullscreen").Invoke();
            Window dialog = app.WaitForTopLevelWindow(E2EFullscreen.ConfirmDialogId, TimeSpan.FromSeconds(5));
            E2EFullscreen.ConfirmButton(dialog, E2EFullscreen.ConfirmShowId).Name.Should().Be("出す");
            Button cancel = E2EFullscreen.ConfirmButton(dialog, E2EFullscreen.ConfirmCancelId);
            cancel.Name.Should().Be("やめる");
            cancel.Properties.HasKeyboardFocus.Value.Should().BeTrue("既定のフォーカスは「やめる」");
            cancel.Invoke();
            E2EAssert.WaitUntil(() => E2EFullscreen.FindConfirmDialog(main) == null, TimeSpan.FromSeconds(5));
            Thread.Sleep(500);
            E2EFullscreen.FindFullscreenWindow(main).Should().BeNull("「やめる」では全画面にしない");
            app.Button("BtnFullscreen").Name.Should().Be("FULLSCREEN");

            // 2 回目: 「出す」で全画面になる。
            E2EFullscreen.Open(main).Should().BeTrue("主画面では確認が出る");
            Window fullscreen = E2EFullscreen.FindFullscreenWindow(main)!;
            E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").Name == "EXIT FULLSCREEN", TimeSpan.FromSeconds(5));

            // Esc で戻る。
            E2EFullscreen.PressEscape(fullscreen);
            E2EAssert.WaitUntil(() => E2EFullscreen.FindFullscreenWindow(main) == null, TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").Name == "FULLSCREEN", TimeSpan.FromSeconds(5));

            app.ExitNormally(TimeSpan.FromSeconds(10)).Should().BeTrue("the application should exit normally");
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public void NonPrimaryDisplay_OpensWithoutConfirm()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        DisplayTarget? secondary = new NativeDisplayCatalog().GetDisplays().FirstOrDefault(d => !d.IsPrimary);
        Skip.If(secondary == null, "2 枚目の画面が無い");

        string directory = NewTempDir("fullscreen-confirm-secondary");
        try
        {
            string settingsPath = WriteSettings(directory, secondary!.DeviceName);
            using var app = E2EAppRunner.Start(exePath, "--vo null", settingsPath);
            Window main = app.MainWindow;
            E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").IsEnabled, TimeSpan.FromSeconds(10));

            app.Button("BtnFullscreen").Invoke();
            E2EAssert.WaitUntil(() => E2EFullscreen.FindFullscreenWindow(main) != null, TimeSpan.FromSeconds(5));
            E2EFullscreen.FindConfirmDialog(main).Should().BeNull("主画面でない出力先では確認を出さない");
            E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").Name == "EXIT FULLSCREEN", TimeSpan.FromSeconds(5));

            app.Button("BtnFullscreen").Invoke();
            E2EAssert.WaitUntil(() => E2EFullscreen.FindFullscreenWindow(main) == null, TimeSpan.FromSeconds(5));
            E2EFullscreen.FindConfirmDialog(main).Should().BeNull("閉じるときも確認は出さない");

            app.ExitNormally(TimeSpan.FromSeconds(10)).Should().BeTrue("the application should exit normally");
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteSettings(string directory, string deviceName)
    {
        string settingsPath = Path.Combine(directory, "settings.json");
        string json = System.Text.Json.JsonSerializer.Serialize(new { fullscreenDisplayDeviceName = deviceName });
        File.WriteAllText(settingsPath, E2ESettingsIsolation.SeedJson(json));
        return settingsPath;
    }
}
