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
    public void ReadLinesSince_LineAtExactlyTheStartTime_IsReturned()
    {
        // 開始の時刻とミリ秒まで同じ行も返す（読み飛ばすのは開始より前の行だけ）。
        DateTime exact = new(2026, 10, 2, 23, 59, 59, 400);

        List<string> lines = AppLogReader.ReadLinesSince(_logDir, exact)
            .Select(line => line.TrimEnd('\r')).ToList();

        lines.Should().StartWith("2026-10-02 23:59:59.400 +09:00 [INF] 終了手順: 新規受付停止");
        lines.Should().HaveCount(4);
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

    // ---- v0.6.4（設計書 9-2）: 前回読んだ位置からの続きの読み（AppLogTail） ----

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static List<string> Trim(IEnumerable<string> lines) => lines.Select(line => line.TrimEnd('\r')).ToList();

    [Fact]
    public void Tail_ContinuesFromTheLastPosition_AcrossTheNewDayFile()
    {
        // 0 時をまたぐ: 前日のファイルだけがある時点で読み始め、前日の追記 → 当日のファイルの新規作成と追記を続きとして読む。
        string dir = Path.Combine(_logDir, "tail");
        Directory.CreateDirectory(dir);
        string previousDay = Path.Combine(dir, "timecodesyncplayer-20261002.log");
        File.WriteAllText(previousDay,
            "2026-10-02 23:59:58.100 +09:00 [INF] before the run\r\n" +
            "2026-10-02 23:59:59.400 +09:00 [INF] Playlist track loaded index=0\r\n");
        var tail = new AppLogTail(dir, RunStarted);

        Trim(tail.ReadNew()).Should().Equal("2026-10-02 23:59:59.400 +09:00 [INF] Playlist track loaded index=0");
        tail.ReadNew().Should().BeEmpty("新しい行が無ければ何も返さない");

        Append(previousDay, "2026-10-02 23:59:59.900 +09:00 [INF] Timecode sync seek ltc=20.000\r\n");
        Trim(tail.ReadNew()).Should().Equal("2026-10-02 23:59:59.900 +09:00 [INF] Timecode sync seek ltc=20.000");

        string today = Path.Combine(dir, "timecodesyncplayer-20261003.log");
        File.WriteAllText(today, "2026-10-03 00:00:00.100 +09:00 [INF] FetchMetadata: 1920x1080 30.000fps\r\n");
        Append(today, "2026-10-03 00:00:00.200 +09:00 [INF] Playlist track loaded index=1\r\n");
        Trim(tail.ReadNew()).Should().Equal(
            "2026-10-03 00:00:00.100 +09:00 [INF] FetchMetadata: 1920x1080 30.000fps",
            "2026-10-03 00:00:00.200 +09:00 [INF] Playlist track loaded index=1");

        Trim(tail.Lines).Should().Equal(Trim(AppLogReader.ReadLinesSince(dir, RunStarted)),
            "続きの読みを積み上げた行は、全体を読み直した行と同じ");
        Trim(tail.LinesSince(new DateTime(2026, 10, 3, 0, 0, 0, 150))).Should().Equal(
            "2026-10-03 00:00:00.200 +09:00 [INF] Playlist track loaded index=1");
    }

    [Fact]
    public void Tail_PartialLine_IsReturnedOnlyAfterItsNewline()
    {
        string dir = Path.Combine(_logDir, "partial");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "timecodesyncplayer-20261002.log");
        File.WriteAllText(log, "2026-10-02 23:59:59.500 +09:00 [INF] FetchMeta");
        var tail = new AppLogTail(dir, RunStarted);

        tail.ReadNew().Should().BeEmpty("書きかけの行（改行の前）は返さない");
        Append(log, "data: 1920x1080 30.000fps\r\n");
        Trim(tail.ReadNew()).Should().Equal("2026-10-02 23:59:59.500 +09:00 [INF] FetchMetadata: 1920x1080 30.000fps");
    }

    [Fact]
    public void Wait_ResolvesAtTheSameStep_AsReadingTheWholeLog()
    {
        // 待ちの判定（WaitForMetadataSince と同じ条件: FetchMetadata の行、または track loaded の行）が解ける段は、
        // 毎回全体を読み直す読み方と続きの読み方で同じ。
        string dir = Path.Combine(_logDir, "wait");
        Directory.CreateDirectory(dir);
        string previousDay = Path.Combine(dir, "timecodesyncplayer-20261002.log");
        string today = Path.Combine(dir, "timecodesyncplayer-20261003.log");
        File.WriteAllText(previousDay, "2026-10-02 23:59:58.000 +09:00 [INF] FetchMetadata: old run\r\n");
        string[][] steps =
        {
            new[] { previousDay, "2026-10-02 23:59:59.100 +09:00 [INF] Gst loadfile path=a.mp4\r\n" },
            new[] { previousDay, "2026-10-02 23:59:59.900 +09:00 [INF] Load path=a.mp4 loadOk=true\r\n" },
            new[] { today, "2026-10-03 00:00:00.050 +09:00 [INF] Sync lifecycle: \"FileLoad\"\r\n" },
            new[] { today, "2026-10-03 00:00:00.300 +09:00 [INF] FetchMetadata: 1920x1080 30.000fps\r\n" },
            new[] { today, "2026-10-03 00:00:00.400 +09:00 [INF] Playlist track loaded index=1\r\n" },
        };
        static bool Resolved(IEnumerable<string> lines) =>
            lines.Any(line => line.Contains("FetchMetadata:", StringComparison.Ordinal));

        var tail = new AppLogTail(dir, RunStarted);
        int wholeStep = -1, tailStep = -1;
        for (int i = 0; i < steps.Length; i++)
        {
            Append(steps[i][0], steps[i][1]);
            if (wholeStep < 0 && Resolved(AppLogReader.ReadLinesSince(dir, RunStarted))) wholeStep = i;
            tail.ReadNew();
            if (tailStep < 0 && Resolved(tail.Lines)) tailStep = i;
        }

        wholeStep.Should().Be(3, "開始より前の FetchMetadata（前の回の行）では解けない");
        tailStep.Should().Be(wholeStep);
    }
}
