using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using Erytheia.Output;
using Erytheia.Tests.Helpers;
using Xunit.Abstractions;

namespace Erytheia.Tests.E2E;

/// <summary>
/// v0.6.8: 内蔵 GPU で動いているときの警告と、GPU の名前の常時表示（設計書 docs/design/v0.6.8-gpu-warning.md の 7.2・7.3）。
/// 開発機では内蔵 GPU で動く条件を作れないので、警告の表示は ERYTHEIA_FORCE_GPU_WARNING で判定の結果を固定して確かめる。
/// 確かめはアプリのログの Information 以上の行（`GPU placement: …`、Warning の `GPU placement warning shown: …`）と UIA で行う。
/// 平常の試験（強制なし）は、想定の機体（GPU が 1 つ、または内蔵＋独立で独立を使用）で警告が 0 回であることを数える。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class GpuPlacementE2ETests(ITestOutputHelper output)
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
            WriteLayout(app, "normal");

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
            // 一覧の高さは画面の大きさで変わるので、最大化して測る（開発機は 1920x1080）。
            app.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Thread.Sleep(800);
            // 開発機の選ばれ方は default（出力先を保存していない）。
            app.Text("GpuPlacementWarningText").Should().Be(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
            FindById(app, "GpuAdapterNameText").Should().BeNull("警告が見えている間は名前を隠す");
            var shownLayout = WriteLayout(app, "warning-shown");

            // 操作を止めない: 窓を新しく出さず、主窓は操作できる（再生のボタンを押すのは復旧の試験で行う。
            // 再生すると LTC の欄の状態の行が出入りして、一覧の高さの比べが揺れるため）。
            app.MainWindow.ModalWindows.Should().BeEmpty("警告はモーダルの画面を出さない");
            app.MainWindow.IsEnabled.Should().BeTrue();
            E2EAssert.WaitUntil(() => app.Button("BtnPlay").IsEnabled, TimeSpan.FromSeconds(15));

            // ×でその起動の間だけ消える。名前の行が出る。
            app.Button("BtnDismissGpuPlacementWarning").Invoke();
            E2EAssert.WaitUntil(() => FindById(app, "GpuPlacementWarningText") == null, TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(() => app.Text("GpuAdapterNameText").StartsWith("GPU: ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            WaitForLine(tail, Dismissed, TimeSpan.FromSeconds(5));
            Thread.Sleep(300);
            var dismissedLayout = WriteLayout(app, "warning-dismissed");

            // 警告が出ている間も、一覧は 1 行以上の高さがあり、行がある。消すと、警告の枠の分だけ一覧が戻る
            // （名前は見出しの行にあるので、消した後の一覧の高さは平常の起動と同じ）。
            shownLayout.PlaylistHeight.Should().BeGreaterThanOrEqualTo(shownLayout.RowHeight,
                "警告が出ている間も一覧は 1 行以上見える");
            if (shownLayout.WindowHeight / shownLayout.Scale >= 1000)
            {
                (shownLayout.PlaylistHeight / shownLayout.Scale).Should().BeGreaterThan(PlaylistMinHeight,
                    "1920x1080 の最大化で、警告が出ていても一覧が最小の高さに当たらない（設計書 5.2）");
            }
            shownLayout.Rows.Should().BeGreaterThan(0);
            // 一覧の最小の高さ（MinHeight=120）で止まるときは、戻る量はそこまで。UIA の矩形は物理ピクセルなので、
            // 論理の値（120・余白 6・許し 2）に倍率を掛けて比べる。
            double scale = shownLayout.Scale;
            int expectedGain = (int)Math.Round(Math.Min(shownLayout.WarningHeight + 6 * scale,
                dismissedLayout.PlaylistHeight - PlaylistMinHeight * scale));
            shownLayout.WarningHeight.Should().BeGreaterThan(0);
            (dismissedLayout.PlaylistHeight - shownLayout.PlaylistHeight)
                .Should().BeCloseTo(expectedGain, (uint)Math.Ceiling(2 * scale), "×で消すと警告の枠（上の余白 6 を含む）の分だけ一覧が戻る");

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

            // 操作を止めない: 警告が出ている間に再生のボタンが押せて、窓は増えない。
            Button play = app.Button("BtnPlay");
            E2EAssert.WaitUntil(() => play.IsEnabled, TimeSpan.FromSeconds(15));
            play.Invoke();
            Thread.Sleep(500);
            play.Invoke();
            app.Process.HasExited.Should().BeFalse();
            app.MainWindow.ModalWindows.Should().BeEmpty("警告はモーダルの画面を出さない");

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

    private const int PlaylistMinHeight = 120;

    /// <summary>矩形は物理ピクセル。Scale は窓の DPI ÷ 96（100% で 1）。</summary>
    private sealed record Layout(int PlaylistHeight, int RowHeight, int Rows, int WarningHeight, int WindowHeight, double Scale);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    /// <summary>一覧の高さと警告の枠の高さを記録する（設計書 5.4 の一覧の見える行数の比べ）。</summary>
    private Layout WriteLayout(E2EAppRunner app, string label)
    {
        ListBox playlist = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PlaylistList")).AsListBox();
        AutomationElement[] items = playlist.Items;
        var rect = playlist.BoundingRectangle;
        int rowHeight = items.Length > 0 ? items[0].BoundingRectangle.Height : 0;
        int warningHeight = FindById(app, "GpuPlacementWarningText")?.BoundingRectangle.Height ?? 0; // Grid は UIA に出ないので、文の高さ
        var window = app.MainWindow.BoundingRectangle;
        string state = app.MainWindow.Patterns.Window.Pattern.WindowVisualState.ValueOrDefault.ToString();
        uint dpi = GetDpiForWindow(app.MainWindow.Properties.NativeWindowHandle.Value);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        output.WriteLine($"layout {label}: window={window.Width}x{window.Height} state={state} playlist={rect} " +
                         $"rows={items.Length} rowHeight={rowHeight} warningPanel={warningHeight} playlistOffscreen={playlist.IsOffscreen} dpi={dpi} scale={scale:F2}");
        return new(rect.Height, rowHeight, items.Length, warningHeight, window.Height, scale);
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
