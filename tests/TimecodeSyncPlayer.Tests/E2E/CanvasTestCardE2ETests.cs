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
/// Gpu が使えない環境（Cpu フォールバック）ではスキップする。実行は依頼者が行う場合がある。
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

    private static string WriteGpuSettings(string workDir)
    {
        string settingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"outputBackend\":1}");
        return settingsPath;
    }

    private static double ParsePositionSeconds(string timeLabelText)
    {
        string current = timeLabelText.Split('/')[0].Trim();
        string[] parts = current.Split(':');
        if (parts.Length < 3) return 0;
        return int.Parse(parts[0]) * 3600
             + int.Parse(parts[1]) * 60
             + int.Parse(parts[2]);
    }

    private static string NewestLogPath(string exeDir)
    {
        string logDir = Path.Combine(exeDir, "logs");
        DirectoryInfo di = new(logDir);
        if (!di.Exists) return Path.Combine(logDir, "timecodesyncplayer-.log");
        FileInfo? newest = di.GetFiles("timecodesyncplayer-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest?.FullName ?? Path.Combine(logDir, "timecodesyncplayer-.log");
    }

    private static string ReadLogFrom(string logPath, long offset)
    {
        if (!File.Exists(logPath)) return "";
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static void WaitForLogAfter(string logPath, long offset, string needle, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ReadLogFrom(logPath, offset).Contains(needle, StringComparison.Ordinal))
                return;
            Thread.Sleep(200);
        }
        ReadLogFrom(logPath, offset).Should().Contain(needle,
            $"アプリログ（{logPath} の offset {offset} 以降）に '{needle}' が記録されるはず");
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
            string settingsPath = WriteGpuSettings(workDir);
            string media = TestVideoFactory.GetOrCreate();
            runner = E2EAppRunner.Start(exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            Button testCard = runner.Button("BtnTestCard");
            Skip.If(!testCard.IsEnabled, "GPU 出力バックエンドが利用できない（Cpu フォールバック）");

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
            string settingsPath = WriteGpuSettings(workDir);
            string sourceMedia = TestVideoFactory.GetOrCreate();
            string media = Path.Combine(workDir, Path.GetFileName(sourceMedia));
            File.Copy(sourceMedia, media, overwrite: true);
            var playlist = new PlaylistState();
            playlist.AddFiles([media]);
            string projectPath = Path.Combine(workDir, "canvas4k.tsp");
            await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Single, GapBehavior.Black,
                new CanvasData { Width = 3840, Height = 2160, DefaultFit = FitHeight.FitId });

            string exeDir = Path.GetDirectoryName(exePath)!;
            string logPath = NewestLogPath(exeDir);
            long logOffset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

            runner = E2EAppRunner.Start(
                exePath, $"--load-project \"{projectPath}\"", settingsPath, pausePlaybackIfNeeded: true);

            WaitForLogAfter(logPath, logOffset, "canvas=3840x2160", TimeSpan.FromSeconds(20));
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
