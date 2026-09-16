using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>D15/O1: shim のログ出力先（TCS_LOG_FILE）の設定と、7 日より古いログの清掃。</summary>
public class GstNativeLogFileTests
{
    [Fact]
    public void ConfigureLogFile_SetsTodayFileAndPrunesOldLogs()
    {
        string? previous = Environment.GetEnvironmentVariable("TCS_LOG_FILE");
        string dir = Path.Combine(Path.GetTempPath(), "tcs-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string oldLog = Path.Combine(dir, "tcs-gst-20200101.log");
        string freshLog = Path.Combine(dir, "tcs-gst-yesterday.log");
        try
        {
            Environment.SetEnvironmentVariable("TCS_LOG_FILE", null);
            File.WriteAllText(oldLog, "old");
            File.WriteAllText(freshLog, "fresh");
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(freshLog, DateTime.UtcNow.AddDays(-1));

            GstNativeLibraryResolver.ConfigureLogFile(dir);

            string? configured = Environment.GetEnvironmentVariable("TCS_LOG_FILE");
            configured.Should().NotBeNull();
            Path.GetDirectoryName(configured).Should().Be(dir);
            Path.GetFileName(configured).Should().Be($"tcs-gst-{DateTime.Now:yyyyMMdd}.log");
            File.Exists(oldLog).Should().BeFalse("7 日より古いログは起動時に消す");
            File.Exists(freshLog).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCS_LOG_FILE", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }

    [Fact]
    public void ConfigureLogFile_DoesNotOverrideUserSetting()
    {
        string? previous = Environment.GetEnvironmentVariable("TCS_LOG_FILE");
        string dir = Path.Combine(Path.GetTempPath(), "tcs-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string configured = Path.Combine(dir, "custom.log");
        try
        {
            Environment.SetEnvironmentVariable("TCS_LOG_FILE", configured);

            GstNativeLibraryResolver.ConfigureLogFile(dir);

            Environment.GetEnvironmentVariable("TCS_LOG_FILE").Should().Be(configured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCS_LOG_FILE", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }
}
