using System.IO;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// 段 2: v0.3 の廃止キー（backend:0 = mpv、outputBackend:0 = Cpu）を含む設定でも
/// 出荷構成（GStreamer + GPU）で起動し、再生できる。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LegacySettingsE2ETests
{
    [SkippableFact(Timeout = 180_000)]
    public void LegacyBackendKeys_BootInShippingConfigAndCanPlay()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = Path.Combine(Path.GetTempPath(), "tcs-legacy-settings-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string settingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(settingsPath, """{"backend":0,"outputBackend":0,"volume":40}""");
        E2EAppRunner? runner = null;
        try
        {
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{TestVideoFactory.GetOrCreate()}\"", settingsPath, pausePlaybackIfNeeded: false);
            E2EAssert.WaitUntil(() => runner.Button("BtnPlay").Name == "⏸", TimeSpan.FromSeconds(20));
            E2EAssert.WaitUntil(() => runner.Text("TimeLabel").Contains('/'), TimeSpan.FromSeconds(10));
            Assert.False(runner.Process.HasExited, "出荷構成で起動し続ける");
        }
        finally
        {
            runner?.Dispose();
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }
}
