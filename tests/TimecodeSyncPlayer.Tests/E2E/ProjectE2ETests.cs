using System;
using System.IO;
using System.Text.Json;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class ProjectE2ETests : IDisposable
{
    private readonly string _projectPath;
    public ProjectE2ETests()
    {
        _projectPath = ProjectFileFactory.CreateTempProjectPath();
    }

    public void Dispose()
    {
        ProjectFileFactory.Cleanup(_projectPath);
    }

    [SkippableFact]
    public void SaveProject_CreatesFileWithTracks()
    {
        var (exe, video, skipReason) = ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);

        using var process = E2EAppRunner.StartProcess(exe, $"--vo null --save-project \"{_projectPath}\" --playlist \"{video}\" \"{video}\"");
        try
        {
            Assert.True(WaitForFile(_projectPath, TimeSpan.FromSeconds(12)),
                $"プロジェクトファイルが生成されなかった: {_projectPath}");
        }
        finally
        {
            E2EAppRunner.KillProcess(process);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(_projectPath));
        Assert.True(document.RootElement.TryGetProperty("tracks", out var tracks), "tracks プロパティが見つからない");
        Assert.True(tracks.GetArrayLength() >= 2, $"保存されたトラック数が不足している: {tracks.GetArrayLength()}");
    }

    [SkippableFact]
    public void LoadProject_RestoresTracks()
    {
        var (exe, video, skipReason) = ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);

        using (var saveProcess = E2EAppRunner.StartProcess(exe, $"--vo null --save-project \"{_projectPath}\" --playlist \"{video}\" \"{video}\""))
        {
            try
            {
                Assert.True(WaitForFile(_projectPath, TimeSpan.FromSeconds(12)),
                    $"保存ステップでプロジェクトファイルが生成されなかった: {_projectPath}");
            }
            finally
            {
                E2EAppRunner.KillProcess(saveProcess);
            }
        }

        Assert.True(new FileInfo(_projectPath).Length > 0, "保存されたプロジェクトファイルが空");

        using var app = E2EAppRunner.Start(exe, $"--vo null --load-project \"{_projectPath}\"");
        Assert.False(app.Process.HasExited, "プロジェクト読込時にアプリが終了した");
    }

    private static (string ExePath, string VideoPath, string? SkipReason) ResolvePrereqs()
    {
        string exe;
        try
        {
            (exe, string? skipReason) = E2EAppRunner.ResolvePrereqs();
            if (skipReason != null)
                return ("", "", skipReason);
        }
        catch (FileNotFoundException ex)
        {
            return ("", "", ex.Message);
        }

        string video;
        try
        {
            video = TestVideoFactory.GetOrCreate();
        }
        catch (Exception ex)
        {
            return (exe, "", $"テスト動画の生成に失敗しました: {ex.Message}");
        }

        return (exe, video, null);
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }
}
