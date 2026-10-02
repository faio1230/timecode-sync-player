using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.4 段 4（設計書 2-3、#21）: E2E がアプリのログを読む共通の口（<see cref="AppLogReader"/>）の単体テスト。
/// CanvasTestCard・ExitDialog・GStreamerBackend・VolumeControl の E2E は、最後に書かれた 1 ファイルだけを読んでいた。
/// 0 時をまたぐ回では前日の 23:59 台の行が前日のファイルに残るので、それも数えること。
/// </summary>
public class AppLogReaderTests : IDisposable
{
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "tcs-app-log-reader", Guid.NewGuid().ToString("N"));

    public AppLogReaderTests()
    {
        Directory.CreateDirectory(_logDir);

        string previousDay = Path.Combine(_logDir, "timecodesyncplayer-20261002.log");
        File.WriteAllText(previousDay,
            "2026-10-02 23:59:58.100 +09:00 [INF] before the run\r\n" +
            "2026-10-02 23:59:59.400 +09:00 [INF] 終了手順: 新規受付停止\r\n" +
            "2026-10-02 23:59:59.700 +09:00 [INF] OutputEngine: 初期化完了 canvas=3840x2160 adapterLuid=1\r\n");
        File.SetLastWriteTime(previousDay, new DateTime(2026, 10, 2, 23, 59, 59, 800));

        string today = Path.Combine(_logDir, "timecodesyncplayer-20261003.log");
        File.WriteAllText(today,
            "2026-10-03 00:00:00.200 +09:00 [INF] 終了手順: GStreamer 停止\r\n" +
            "2026-10-03 00:00:00.300 +09:00 [INF] Playlist track loaded index=1\r\n");
        File.SetLastWriteTime(today, new DateTime(2026, 10, 3, 0, 0, 1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime RunStarted = new(2026, 10, 2, 23, 59, 59, 0);

    [Fact]
    public void ReadTextSince_AcrossMidnight_FindsTheLinesOfThePreviousDayFile()
    {
        string text = AppLogReader.ReadTextSince(_logDir, RunStarted);

        text.Should().Contain("canvas=3840x2160", "前日の 23:59 台の行も読む（最新の 1 ファイルだけだと落ちる）");
        text.Should().Contain("終了手順: 新規受付停止");
        text.Should().Contain("終了手順: GStreamer 停止");
        text.Should().NotContain("before the run", "開始より前の行は読まない");
    }

    [Fact]
    public void ReadLinesSince_AcrossMidnight_ReturnsBothDaysInOrder()
    {
        List<string> lines = AppLogReader.ReadLinesSince(_logDir, RunStarted)
            .Select(line => line.TrimEnd('\r')).ToList();

        lines.Should().Equal(
            "2026-10-02 23:59:59.400 +09:00 [INF] 終了手順: 新規受付停止",
            "2026-10-02 23:59:59.700 +09:00 [INF] OutputEngine: 初期化完了 canvas=3840x2160 adapterLuid=1",
            "2026-10-03 00:00:00.200 +09:00 [INF] 終了手順: GStreamer 停止",
            "2026-10-03 00:00:00.300 +09:00 [INF] Playlist track loaded index=1");
    }
}
