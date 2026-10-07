using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 R-11: Spout を ON にすると出力の欄に「Spout: 送信名」が出て、OFF で消える（既定名）。
/// 設定は一時の場所に切り離す（E2ESettingsIsolation）。送信名の環境変数は子プロセスから外して既定名にする。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class SpoutStatusE2ETests
{
    [SkippableFact(Timeout = 120_000)]
    public void SpoutToggle_ShowsAndHidesSenderNameInOutputStatus()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string exeDir = Path.GetDirectoryName(prereqs.ExePath)!;
        Skip.If(!File.Exists(Path.Combine(exeDir, "SpoutDX.dll")), "SpoutDX.dll が出力に無い（native\\SpoutDX.dll を置いてビルドする）");

        string directory = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "spout-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        var environment = new Dictionary<string, string?> { [SpoutDefaults.SenderNameEnvironmentVariable] = null };
        string expected = "Spout: " + SpoutDefaults.DefaultSenderName;

        try
        {
            using var app = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath, environment: environment);
            Button spout = app.Button("BtnSpout");
            E2EAssert.WaitUntil(() => spout.IsEnabled, TimeSpan.FromSeconds(10));
            app.Text("BtnSpout").Should().Be("Spout OFF");
            app.Text("SpoutStatusText").Should().BeEmpty("OFF のときは状態の文字を出さない");

            spout.Invoke();
            E2EAssert.WaitUntil(() => app.Text("SpoutStatusText") == expected, TimeSpan.FromSeconds(5));
            app.Text("BtnSpout").Should().Be("Spout ON");
            app.Text("SpoutStatusText").Should().Be(expected);

            spout.Invoke();
            E2EAssert.WaitUntil(() => app.Text("SpoutStatusText") == "", TimeSpan.FromSeconds(5));
            app.Text("BtnSpout").Should().Be("Spout OFF");

            spout.Invoke();
            E2EAssert.WaitUntil(() => app.Text("SpoutStatusText") == expected, TimeSpan.FromSeconds(5));

            app.ExitNormally(TimeSpan.FromSeconds(10)).Should().BeTrue("the application should exit normally");
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }
}
