using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Erytheia.Output;
using Erytheia.Tests.Helpers;

namespace Erytheia.Tests.E2E;

/// <summary>
/// v0.6.8: 内蔵 GPU で動いているときの警告と、GPU の名前の常時表示（設計書 docs/design/v0.6.8-gpu-warning.md の 7.2・7.3）。
/// 開発機では内蔵 GPU で動く条件を作れないので、警告の表示は ERYTHEIA_FORCE_GPU_WARNING で判定の結果を固定して確かめる。
/// 確かめはアプリのログの Information 以上の行（`GPU placement: …`、Warning の `GPU placement warning shown: …`）と UIA で行う。
/// 平常の試験（強制なし）は、想定の機体（GPU が 1 つ、または内蔵＋独立で独立を使用）で警告が 0 回であることを数える。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class GpuPlacementE2ETests
{
    private const string StartupLine = "GPU placement: at=startup ";
    private const string RecoveryLine = "GPU placement: at=recovery ";
    private const string WarningShown = "GPU placement warning shown: ";
    private const string Failed = "GPU placement failed: ";
    private const string Dismissed = "GPU placement warning dismissed";
    private const string Injection = "検証用の注入: " + GpuPlacementPolicy.ForceWarningEnvironmentVariable;

    [SkippableFact(Timeout = 180_000)]
    public void NormalStartupAndRecovery_ShowNameWithoutWarning()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("gpu-placement-normal");
        DateTime since = DateTime.Now; // 前の試験のアプリの行を数えない（-1 秒にすると直前の復旧の行が入る）
        var tail = new AppLogTail(AppLogReader.LogDirectoryForExe(exePath), since);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [GpuPlacementPolicy.ForceWarningEnvironmentVariable] = null,
                [OutputEngineSettings.SimulateDeviceLossEnvironmentVariable] = "6",
            };
            string media = TestVideoFactory.GetOrCreate();
            using var app = E2EAppRunner.Start(exePath, $"--open \"{media}\"",
                Path.Combine(workDir, "settings.json"), environment: environment);

            WaitForLine(tail, StartupLine, TimeSpan.FromSeconds(20));
            E2EAssert.WaitUntil(() => app.Text("GpuAdapterNameText").StartsWith("GPU: ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            FindById(app, "GpuPlacementWarningText").Should().BeNull("想定の機体では警告を出さない（発火 0）");

            // 復旧の後に判定し直す。名前は残り、警告は出ない。
            WaitForLine(tail, RecoveryLine, TimeSpan.FromSeconds(40));
            E2EAssert.WaitUntil(() => app.Text("GpuAdapterNameText").StartsWith("GPU: ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            FindById(app, "GpuPlacementWarningText").Should().BeNull();

            app.ExitNormally(TimeSpan.FromSeconds(20)).Should().BeTrue("the application should exit normally");
            IReadOnlyList<string> lines = ReadAll(tail);
            Count(lines, StartupLine).Should().Be(1, "起動 1 回に判定の行は 1 行");
            Count(lines, RecoveryLine).Should().Be(1, "復旧 1 回に判定の行は 1 行");
            Count(lines, "OutputEngine: GPU 復旧が完了").Should().Be(1);
            Count(lines, WarningShown).Should().Be(0, "想定の機体では警告を出さない（発火 0）");
            Count(lines, Failed).Should().Be(0);
            Count(lines, Injection).Should().Be(0, "強制の変数が無いときは注入の行を出さない");
        }
        finally
        {
            E2ESettingsIsolation.Delete(workDir);
        }
    }

    [SkippableFact(Timeout = 180_000)]
    public void ForcedWarning_DoesNotBlockOperationAndDismissesForThisRun()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("gpu-placement-forced");
        DateTime since = DateTime.Now; // 前の試験のアプリの行を数えない（-1 秒にすると直前の復旧の行が入る）
        var tail = new AppLogTail(AppLogReader.LogDirectoryForExe(exePath), since);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [GpuPlacementPolicy.ForceWarningEnvironmentVariable] = "1",
                [OutputEngineSettings.SimulateDeviceLossEnvironmentVariable] = null,
            };
            string media = TestVideoFactory.GetOrCreate();
            using var app = E2EAppRunner.Start(exePath, $"--open \"{media}\"",
                Path.Combine(workDir, "settings.json"), environment: environment);

            WaitForLine(tail, WarningShown, TimeSpan.FromSeconds(20));
            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") != null, TimeSpan.FromSeconds(10));
            app.Text("GpuPlacementWarningText").Should().StartWith("⚠ 内蔵 GPU（")
                .And.Contain("ERYTHEIA.exe を「高パフォーマンス」にして");
            FindById(app, "GpuAdapterNameText").Should().BeNull("警告が見えている間は名前の行を隠す");

            // 操作を止めない: 窓を新しく出さず、主窓は操作できる。再生のボタンが押せる。
            app.MainWindow.ModalWindows.Should().BeEmpty("警告はモーダルの画面を出さない");
            app.MainWindow.IsEnabled.Should().BeTrue();
            Button play = app.Button("BtnPlay");
            E2EAssert.WaitUntil(() => play.IsEnabled, TimeSpan.FromSeconds(15));
            play.Invoke();
            Thread.Sleep(500);
            play.Invoke();
            app.Process.HasExited.Should().BeFalse();
            app.MainWindow.ModalWindows.Should().BeEmpty();

            // ×でその起動の間だけ消える。名前の行が出る。
            app.Button("BtnDismissGpuPlacementWarning").Invoke();
            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") == null, TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(() => app.Text("GpuAdapterNameText").StartsWith("GPU: ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            WaitForLine(tail, Dismissed, TimeSpan.FromSeconds(5));

            app.ExitNormally(TimeSpan.FromSeconds(20)).Should().BeTrue("the application should exit normally");
            IReadOnlyList<string> lines = ReadAll(tail);
            Count(lines, StartupLine).Should().Be(1);
            lines.Single(line => line.Contains(StartupLine, StringComparison.Ordinal))
                .TrimEnd().Should().EndWith("verdict=IntegratedInUse reason=forced");
            Count(lines, WarningShown).Should().Be(1);
            Count(lines, Injection).Should().Be(1, "強制が効いた起動では注入の Warning を出す");
            Count(lines, Failed).Should().Be(0);
        }
        finally
        {
            E2ESettingsIsolation.Delete(workDir);
        }
    }

    [SkippableFact(Timeout = 180_000)]
    public void ForcedWarning_DismissedThenDeviceLossRecovery_ShowsAgain()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("gpu-placement-recovery");
        DateTime since = DateTime.Now; // 前の試験のアプリの行を数えない（-1 秒にすると直前の復旧の行が入る）
        var tail = new AppLogTail(AppLogReader.LogDirectoryForExe(exePath), since);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [GpuPlacementPolicy.ForceWarningEnvironmentVariable] = "1",
                [OutputEngineSettings.SimulateDeviceLossEnvironmentVariable] = "8",
            };
            string media = TestVideoFactory.GetOrCreate();
            using var app = E2EAppRunner.Start(exePath, $"--open \"{media}\"",
                Path.Combine(workDir, "settings.json"), environment: environment);

            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") != null, TimeSpan.FromSeconds(20));
            app.Button("BtnDismissGpuPlacementWarning").Invoke();
            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") == null, TimeSpan.FromSeconds(5));
            Count(ReadAll(tail), RecoveryLine).Should().Be(0, "消したのは復旧より前（模擬の消失は起動から 8 秒）");

            // 復旧の後の判定で IntegratedInUse なら、消していても、また出す。
            WaitForLine(tail, RecoveryLine, TimeSpan.FromSeconds(40));
            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") != null, TimeSpan.FromSeconds(10));

            app.ExitNormally(TimeSpan.FromSeconds(20)).Should().BeTrue("the application should exit normally");
            IReadOnlyList<string> lines = ReadAll(tail);
            Count(lines, StartupLine).Should().Be(1);
            Count(lines, RecoveryLine).Should().Be(1);
            Count(lines, WarningShown).Should().Be(2, "起動と復旧の後に 1 回ずつ");
            Count(lines, "OutputEngine: GPU 復旧が完了").Should().Be(1);
            Count(lines, Failed).Should().Be(0);
        }
        finally
        {
            E2ESettingsIsolation.Delete(workDir);
        }
    }

    private static AutomationElement? FindById(E2EAppRunner app, string automationId)
        => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private static void WaitForLine(AppLogTail tail, string needle, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (tail.ReadText().Contains(needle, StringComparison.Ordinal))
                return;
            Thread.Sleep(200);
        }
        tail.ReadText().Should().Contain(needle,
            $"アプリログ（{tail.SinceLocal:HH:mm:ss.fff} 以降）に '{needle}' が記録されるはず");
    }

    private static IReadOnlyList<string> ReadAll(AppLogTail tail)
    {
        tail.ReadNew();
        return tail.Lines;
    }

    private static int Count(IReadOnlyList<string> lines, string needle)
        => lines.Count(line => line.Contains(needle, StringComparison.Ordinal));

    private static string NewTempDir(string name)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Erytheia.Tests", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
