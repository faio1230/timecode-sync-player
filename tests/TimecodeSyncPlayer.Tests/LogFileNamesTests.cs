using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.7: ログのファイルの名前（新しい erytheia-*、旧の timecodesyncplayer-*・tcs-gst-*）の見分け。</summary>
public class LogFileNamesTests
{
    [Theory]
    [InlineData("erytheia-20261113.log", true, false, false)]
    [InlineData("erytheia-20261113_001.log", true, false, false)]
    [InlineData("erytheia-gst-20261113.log", false, true, true)]
    [InlineData("timecodesyncplayer-20261113.log", true, false, true)]
    [InlineData("tcs-gst-20261113.log", false, true, true)]
    [InlineData("erytheia-20261113.txt", false, false, false)]
    [InlineData("other.log", false, false, false)]
    public void Classifies_TheLogFiles(string name, bool app, bool shim, bool pruned)
    {
        LogFileNames.IsAppLog(name).Should().Be(app);
        LogFileNames.IsShimLog(name).Should().Be(shim);
        LogFileNames.IsPrunedAtStartup(name).Should().Be(pruned);
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
            "timecodesyncplayer-20261113.log",
            "erytheia-20261112.log",
            "timecodesyncplayer-20261111.log",
        };

        names.OrderBy(LogFileNames.AppLogSortKey, StringComparer.OrdinalIgnoreCase).Should().Equal(
            "timecodesyncplayer-20261111.log",
            "erytheia-20261112.log",
            "timecodesyncplayer-20261113.log",
            "erytheia-20261113.log",
            "erytheia-20261113_001.log");
    }
}
