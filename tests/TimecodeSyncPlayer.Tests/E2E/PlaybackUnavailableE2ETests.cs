using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// R1 1-2 の E2E: GPU 出力が使えないときにダイアログを 1 回だけ出し、アプリは閉じず
/// 再生だけを無効にする。失敗は環境変数で注入する（製品の設定項目は増やさない）。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaybackUnavailableE2ETests
{
    private const string DialogTitle = "映像出力を利用できません";

    [SkippableFact(Timeout = 180_000)]
    public void GpuUnavailable_ShowsDialogOnceAndKeepsAppOpenWithPlaybackDisabled()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = Path.Combine(
            Path.GetTempPath(), "tcs-gpu-unavailable-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        E2EAppRunner? runner = null;
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [OutputBackendState.ForceUnavailableEnvironmentVariable] = "1",
            };

            runner = E2EAppRunner.Start(
                exePath, "", settingsFilePath: null, pausePlaybackIfNeeded: false, environment: environment);

            // 起動時のダイアログが 1 回出る。OK だけで閉じる。
            E2EAssert.WaitUntil(() => runner.FindWindowByName(DialogTitle) != null, TimeSpan.FromSeconds(20));
            Window dialog = runner.FindWindowByName(DialogTitle)!;
            dialog.Name.Should().Contain(DialogTitle);
            dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(element => element.AsButton())
                .First(button => button.IsEnabled)
                .Invoke();

            // アプリは開いたまま。映像領域に利用不可の表示が出る。
            E2EAssert.WaitUntil(
                () => runner.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PlaybackUnavailableText")) != null,
                TimeSpan.FromSeconds(10));

            // 再生ボタンを押してもクラッシュしない（再生は行わない）。
            runner.Button("BtnPlay").Invoke();
            Thread.Sleep(2000);
            runner.FindWindowByName(DialogTitle).Should().BeNull("起動ごとのダイアログは 1 回だけ");
            runner.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PlaybackUnavailableText"))
                .Should().NotBeNull("アプリは閉じずに利用不可の表示を続ける");
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDirectory(workDir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp cleanup only
        }
    }
}
