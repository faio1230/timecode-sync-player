using System.IO;
using FluentAssertions;

namespace Erytheia.Tests;

/// <summary>v0.6.7: ログのファイルの名前（新しい erytheia-*、旧の timecodesyncplayer-*・tcs-gst-*）の見分け。</summary>
public class LogFileNamesTests
{
    [Theory]
    [InlineData("erytheia-20261113.log", true, false, 0)]
    [InlineData("erytheia-20261113_001.log", true, false, 0)]
    [InlineData("erytheia-gst-20261113.log", false, true, 7)]
    [InlineData("timecodesyncplayer-20261113.log", true, false, 31)]
    [InlineData("tcs-gst-20261113.log", false, true, 7)]
    [InlineData("erytheia-20261113.txt", false, false, 0)]
    [InlineData("other.log", false, false, 0)]
    public void Classifies_TheLogFiles(string name, bool app, bool shim, int retentionDays)
    {
        // retentionDays 0: 起動時の掃除の対象でない。
        LogFileNames.IsAppLog(name).Should().Be(app);
        LogFileNames.IsShimLog(name).Should().Be(shim);
        LogFileNames.StartupRetention(name).Should().Be(retentionDays == 0 ? null : TimeSpan.FromDays(retentionDays));
    }

    [Fact]
    public void Names_UseTheNewPrefixes()
    {
        var day = new DateTime(2026, 11, 13);
        LogFileNames.AppFileName(day).Should().Be("erytheia-20261113.log");
        LogFileNames.ShimFileName(day).Should().Be("erytheia-gst-20261113.log");
        Path.GetFileName(LogFileNames.SerilogPath("logs")).Should().Be("erytheia-.log");
    }

    [Fact]
    public void AppLogSortKey_OrdersByDayThenOldNameFirst()
    {
        string[] names =
        {
            "erytheia-20261113_001.log",
            "erytheia-20261113.log",
            "timecodesyncplayer-20261113_001.log",
            "timecodesyncplayer-20261113.log",
            "erytheia-20261112.log",
            "timecodesyncplayer-20261111.log",
        };

        names.OrderBy(LogFileNames.AppLogSortKey, StringComparer.OrdinalIgnoreCase).Should().Equal(
            "timecodesyncplayer-20261111.log",
            "erytheia-20261112.log",
            "timecodesyncplayer-20261113.log",
            "timecodesyncplayer-20261113_001.log",
            "erytheia-20261113.log",
            "erytheia-20261113_001.log");
    }
}
