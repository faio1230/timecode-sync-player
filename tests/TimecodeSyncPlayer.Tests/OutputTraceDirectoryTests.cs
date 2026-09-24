using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D34: 出力トレースを同じディレクトリへ繰り返し保存しても manifest.json と衝突しない。
/// </summary>
public class OutputTraceDirectoryTests
{
    [Fact]
    public void ResolveRunDirectory_KeepsBaseWhenNoTraceExists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-dir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            OutputTrace.ResolveRunDirectory(dir).Should().Be(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }

    [Fact]
    public void ResolveRunDirectory_UsesTimestampPidSubdirectoryWhenManifestExists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-dir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        try
        {
            string first = OutputTrace.ResolveRunDirectory(dir);

            first.Should().NotBe(dir);
            Path.GetDirectoryName(first).Should().Be(dir);
            string name = Path.GetFileName(first);
            name.Should().StartWith($"{DateTime.UtcNow:yyyyMMdd}T");
            name.Should().EndWith($"-{Environment.ProcessId}");

            // 実行フォルダが既にある場合は連番を付けて衝突しない。
            Directory.CreateDirectory(first);
            string second = OutputTrace.ResolveRunDirectory(dir);
            second.Should().NotBe(first);
            Path.GetDirectoryName(second).Should().Be(dir);
            Path.GetFileName(second).Should().EndWith($"-{Environment.ProcessId}-2");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }

    [Fact]
    public void PruneOldRuns_DeletesOldestRunsOverTheLimit_KeepsNewestAndRootTrace()
    {
        // v0.5.1: 起動のたびに増えるトレースで開発機のディスクが埋まった。古い回から消す。
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-dir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");     // 置き場の直下のトレース
        string MakeRun(string name, int bytes, DateTime created)
        {
            string run = Path.Combine(dir, name);
            Directory.CreateDirectory(run);
            File.WriteAllBytes(Path.Combine(run, "events.jsonl"), new byte[bytes]);
            Directory.SetCreationTimeUtc(run, created);
            return run;
        }
        try
        {
            var t0 = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
            string oldest = MakeRun("a", 400, t0);
            string middle = MakeRun("b", 400, t0.AddMinutes(1));
            string newest = MakeRun("c", 2000, t0.AddMinutes(2));        // 1 回で上限を超える
            string notATrace = Path.Combine(dir, "notes");
            Directory.CreateDirectory(notATrace);

            OutputTrace.PruneOldRuns(dir, retainedBytes: 1000);

            Directory.Exists(newest).Should().BeTrue("いちばん新しい回は大きくても残す");
            Directory.Exists(middle).Should().BeFalse();
            Directory.Exists(oldest).Should().BeFalse();
            Directory.Exists(notATrace).Should().BeTrue("トレースでないフォルダには触れない");
            File.Exists(Path.Combine(dir, "manifest.json")).Should().BeTrue("直下のトレースには触れない");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }

    [Fact]
    public void PruneOldRuns_UnderTheLimit_KeepsEverything()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-dir", Guid.NewGuid().ToString("N"));
        string run = Path.Combine(dir, "a");
        Directory.CreateDirectory(run);
        File.WriteAllBytes(Path.Combine(run, "events.jsonl"), new byte[100]);
        try
        {
            OutputTrace.PruneOldRuns(dir, retainedBytes: 1000);
            Directory.Exists(run).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }

    [Fact]
    public void Create_UsesSubdirectoryWhenBaseAlreadyHasTrace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-dir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
        try
        {
            OutputTrace trace = OutputTrace.Create(dir);

            trace.IsEnabled.Should().BeTrue();
            trace.Directory.Should().NotBeNullOrEmpty();
            trace.Directory.Should().NotBe(dir);
            Path.GetDirectoryName(trace.Directory!).Should().Be(Path.GetFullPath(dir));
            Directory.Exists(trace.Directory).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }
}
