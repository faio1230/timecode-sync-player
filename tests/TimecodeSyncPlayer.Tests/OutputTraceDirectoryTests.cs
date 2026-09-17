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
