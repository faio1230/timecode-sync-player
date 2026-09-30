using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.E2E;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// LTC シナリオの E2E がアプリのログを読む口（<see cref="LtcScenarioE2ETests.ReadLogLinesSince"/>）の単体テスト。
/// Serilog は日次でファイルを切り替えるので、0 時をまたぐ回の行は前日と当日の 2 ファイルに分かれる
/// （検証機の v0.5.5 の R-5 で、最後に書かれた 1 ファイルだけを読み、前日の 23:59:59 の行を落とした）。
/// </summary>
public class LtcScenarioLogReaderTests
{
    [Fact]
    public void ReadLogLinesSince_AcrossMidnight_ReadsThePreviousDayAndTodayInOrder()
    {
        string logDir = Path.Combine(Path.GetTempPath(), "tcs-log-reader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        try
        {
            string previousDay = Path.Combine(logDir, "timecodesyncplayer-20260930.log");
            File.WriteAllText(previousDay,
                "2026-09-30 23:59:58.900 +09:00 [INF] before the run\r\n" +
                "2026-09-30 23:59:59.228 +09:00 [INF] LTC timecode held: landing seek issued target=20.000\r\n");
            File.SetLastWriteTime(previousDay, new DateTime(2026, 9, 30, 23, 59, 59, 300));

            string today = Path.Combine(logDir, "timecodesyncplayer-20261001.log");
            File.WriteAllText(today,
                "2026-10-01 00:00:00.500 +09:00 [INF] Timecode sync seek ltc=21.000\r\n");
            File.SetLastWriteTime(today, new DateTime(2026, 10, 1, 0, 0, 1));

            // 前々日のファイル（sinceLocal の日付より前に書かれた）は読まない。
            string older = Path.Combine(logDir, "timecodesyncplayer-20260929.log");
            File.WriteAllText(older, "2026-09-29 12:00:00.000 +09:00 [INF] older\r\n");
            File.SetLastWriteTime(older, new DateTime(2026, 9, 29, 12, 0, 1));

            DateTime sinceLocal = new(2026, 9, 30, 23, 59, 59, 0);

            List<string> lines = LtcScenarioE2ETests.ReadLogLinesSince(logDir, sinceLocal)
                .Select(line => line.TrimEnd('\r')).ToList();

            lines.Should().Equal(
                "2026-09-30 23:59:59.228 +09:00 [INF] LTC timecode held: landing seek issued target=20.000",
                "2026-10-01 00:00:00.500 +09:00 [INF] Timecode sync seek ltc=21.000");
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }
}
