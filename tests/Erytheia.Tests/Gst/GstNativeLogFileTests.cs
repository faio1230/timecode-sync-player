using System.IO;
using FluentAssertions;
using Erytheia.Gst;

namespace Erytheia.Tests.Gst;

/// <summary>D15/O1: shim のログ出力先（ERYTHEIA_LOG_FILE）の設定と、7 日より古いログの清掃。</summary>
public class GstNativeLogFileTests
{
    [Fact]
    public void ConfigureLogFile_SetsTodayFileAndPrunesOldLogs()
    {
        string? previous = Environment.GetEnvironmentVariable("ERYTHEIA_LOG_FILE");
        string dir = Path.Combine(Path.GetTempPath(), "tcs-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string oldLog = Path.Combine(dir, "erytheia-gst-20200101.log");
        string freshLog = Path.Combine(dir, "erytheia-gst-yesterday.log");
        // v0.6.7: v0.6.6 までの名前の shim のログも 7 日で消す。v0.6.6 までの名前のアプリのログは 31 日まで残す
        // （v0.6.6 までは Serilog の既定の 31 ファイルで残っていた）。新しいアプリのログは Serilog に任せる。
        string legacyShimLog = Path.Combine(dir, "tcs-gst-20200101.log");
        string legacyAppLog = Path.Combine(dir, "timecodesyncplayer-20200101.log");
        string legacyAppLog8Days = Path.Combine(dir, "timecodesyncplayer-20200102.log");
        string newAppLog = Path.Combine(dir, "erytheia-20200101.log");
        try
        {
            Environment.SetEnvironmentVariable("ERYTHEIA_LOG_FILE", null);
            File.WriteAllText(oldLog, "old");
            File.WriteAllText(freshLog, "fresh");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(freshLog, DateTime.UtcNow.AddDays(-1));
            foreach (string path in new[] { legacyShimLog, legacyAppLog8Days, newAppLog })
            {
                File.WriteAllText(path, "old");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            }
            File.WriteAllText(legacyAppLog, "old");
            File.SetLastWriteTimeUtc(legacyAppLog, DateTime.UtcNow.AddDays(-32));

            GstNativeLibraryResolver.ConfigureLogFile(dir);

            string? configured = Environment.GetEnvironmentVariable("ERYTHEIA_LOG_FILE");
            configured.Should().NotBeNull();
            Path.GetDirectoryName(configured).Should().Be(dir);
            Path.GetFileName(configured).Should().Be($"erytheia-gst-{DateTime.Now:yyyyMMdd}.log");
            File.Exists(oldLog).Should().BeFalse("7 日より古いログは起動時に消す");
            File.Exists(freshLog).Should().BeTrue();
            File.Exists(legacyShimLog).Should().BeFalse("旧の名前の shim のログも消す");
            File.Exists(legacyAppLog8Days).Should().BeTrue("8 日前の旧の名前のアプリのログは残す（31 日まで）");
            File.Exists(legacyAppLog).Should().BeFalse("31 日より古い旧の名前のアプリのログは消す");
            File.Exists(newAppLog).Should().BeTrue("新しいアプリのログは Serilog が数を保つ");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERYTHEIA_LOG_FILE", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }

    [Fact]
    public void ConfigureLogFile_DoesNotOverrideUserSetting()
    {
        string? previous = Environment.GetEnvironmentVariable("ERYTHEIA_LOG_FILE");
        string dir = Path.Combine(Path.GetTempPath(), "tcs-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configured = Path.Combine(dir, "custom.log");
        try
        {
            Environment.SetEnvironmentVariable("ERYTHEIA_LOG_FILE", configured);

            GstNativeLibraryResolver.ConfigureLogFile(dir);

            Environment.GetEnvironmentVariable("ERYTHEIA_LOG_FILE").Should().Be(configured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERYTHEIA_LOG_FILE", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }
}
