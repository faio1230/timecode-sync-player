using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// 段階 4.6 の E2E（Gpu backend）。
///  - 再生中のテストカード ON/OFF で TimeLabel が進み続け、BtnPlay の状態が変わらない
///  - 4K キャンバスのプロジェクトを開くとログに canvas=3840x2160 が出る
/// Gpu が使えない環境（再生無効）ではスキップする。実行は依頼者が行う場合がある。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class CanvasTestCardE2ETests
{
    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ShippingSettingsPath(string workDir) =>
        Path.Combine(workDir, "settings.json");

    private static double ParsePositionSeconds(string timeLabelText)
    {
        string current = timeLabelText.Split('/')[0].Trim();
        string[] parts = current.Split(':');
        if (parts.Length < 3) return 0;
        return int.Parse(parts[0]) * 3600
             + int.Parse(parts[1]) * 60
             + int.Parse(parts[2]);
    }

    // v0.6.4 段 4: 最新の 1 ファイルの末尾からではなく、開始の時刻以降のすべての日のファイルを読む
    // （0 時をまたぐ回で前日のファイルの行を落とさない。AppLogReader）。
    // v0.6.4（設計書 9-2）: 待ちのたびに全体を読み直さず、前回読んだ位置からの続きを読む（AppLogTail）。
    private static void WaitForLogAfter(string logDir, DateTime sinceLocal, string needle, TimeSpan timeout)
    {
        var tail = new AppLogTail(logDir, sinceLocal);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (tail.ReadText().Contains(needle, StringComparison.Ordinal))
                return;
            Thread.Sleep(200);
        }
        tail.ReadText().Should().Contain(needle,
            $"アプリログ（{logDir} の {sinceLocal:HH:mm:ss.fff} 以降）に '{needle}' が記録されるはず");
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task TestCardToggle_DuringPlayback_KeepsPlayingAndPlayState()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("tcs-canvas-e2e");
        E2EAppRunner? runner = null;
        try
        {
            string settingsPath = ShippingSettingsPath(workDir);
            string media = TestVideoFactory.GetOrCreate();
            runner = E2EAppRunner.Start(exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            Button testCard = runner.Button("BtnTestCard");
            Skip.If(!testCard.IsEnabled, "GPU 出力が利用できない（再生は無効）");

            Button play = runner.Button("BtnPlay");
            if (play.Name != "⏸")
            {
                play.Invoke();
                E2EAssert.WaitUntil(() => play.Name == "⏸", TimeSpan.FromSeconds(3));
            }
            string playStateBefore = play.Name;

            testCard.Invoke();
            E2EAssert.WaitUntil(() => runner.Text("BtnTestCard").Contains("ON", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            string beforeOn = runner.Text("TimeLabel");
            await Task.Delay(1500);
            string afterOn = runner.Text("TimeLabel");

            play.Name.Should().Be(playStateBefore, "カード ON は再生状態を変えない");
            ParsePositionSeconds(afterOn).Should().BeGreaterThan(ParsePositionSeconds(beforeOn),
                "カード ON 中も TimeLabel が進み続ける");

            testCard.Invoke();
            E2EAssert.WaitUntil(() => runner.Text("BtnTestCard").Contains("OFF", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            string beforeOff = runner.Text("TimeLabel");
            await Task.Delay(1200);
            string afterOff = runner.Text("TimeLabel");

            play.Name.Should().Be(playStateBefore, "カード OFF は再生状態を変えない");
            ParsePositionSeconds(afterOff).Should().BeGreaterThan(ParsePositionSeconds(beforeOff),
                "カード OFF 後も TimeLabel が進み続ける");
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task ProjectWith4KCanvas_LogsCanvasSize()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("tcs-canvas4k-e2e");
        E2EAppRunner? runner = null;
        try
        {
            string settingsPath = ShippingSettingsPath(workDir);
            string sourceMedia = TestVideoFactory.GetOrCreate();
            string media = Path.Combine(workDir, Path.GetFileName(sourceMedia));
            File.Copy(sourceMedia, media, overwrite: true);
            var playlist = new PlaylistState();
            playlist.AddFiles([media]);
            string projectPath = Path.Combine(workDir, "canvas4k.tsp");
            await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Single, GapBehavior.Black,
                new CanvasData { Width = 3840, Height = 2160, DefaultFit = FitHeight.FitId });

            string logDir = AppLogReader.LogDirectoryForExe(exePath);
            DateTime logSince = DateTime.Now;

            runner = E2EAppRunner.Start(
                exePath, $"--load-project \"{projectPath}\"", settingsPath, pausePlaybackIfNeeded: true);

            WaitForLogAfter(logDir, logSince, "canvas=3840x2160", TimeSpan.FromSeconds(20));
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
