using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.4.8 hotfix: 外部ツールが UI Automation で LTC 表示・再生位置表示を高頻度に読んでも、
/// 読み取りそのものは**表示用の文字列を返すだけ**で、再生側へは何も起こさないこと。
/// <list type="bullet">
/// <item>LTC 表示・位置表示の読み取りが GStreamer の位置照会・シーク・速度変更・同期判定を起こさない（テスト 1・2）</item>
/// <item>読み続けても同期状態（シーク・速度変更）が変わらない（テスト 3）</item>
/// <item>読み取りで UI スレッドが遅れても、合成・配信・新しいフレームの採用は続く（テスト 4）</item>
/// </list>
/// 判定はアプリの出力トレース（shim の位置照会 gst.position・seek.issue・player.seeking・sync.evaluate）で行う。
/// 読み取りの区間と、同じ長さの読まない区間を比べる。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class UiaReadSideEffectE2ETests
{
    private static readonly string[] ReadIds = ["LtcTimecodeText", "LtcRealTimeText", "TimeLabel", "MetaLineText"];
    private readonly ITestOutputHelper _output;

    public UiaReadSideEffectE2ETests(ITestOutputHelper output) => _output = output;

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void HighRateUiaReads_DoNotQueryOrDrivePlayback(bool playing)
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);

        string videoPath = TestVideoFactory.GetOrCreate();
        string traceDir = Path.Combine(Path.GetTempPath(), $"tcs-uia-read-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(traceDir);
        var environment = new Dictionary<string, string?> { ["TIMECODE_SYNC_PLAYER_OUTPUT_TRACE"] = traceDir };

        long idleStart, idleEnd, readStart, readEnd;
        int reads = 0;
        using (E2EAppRunner app = E2EAppRunner.Start(
                   exePath, $"--open \"{videoPath}\"", settingsFilePath: null,
                   pausePlaybackIfNeeded: !playing, environment: environment))
        {
            if (playing)
                E2EAssert.WaitUntil(() => app.Button("BtnPlay").Name == "⏸", TimeSpan.FromSeconds(5));
            Thread.Sleep(2000);

            // 区間の長さは TCS_UIA_READ_SECONDS で伸ばせる（GPU 使用率などを外から並べて測るとき用）。
            // テスト動画は 20 秒なので、待機 2 秒 + 2 区間が収まる 8 秒までにする。
            double phaseSeconds = double.TryParse(Environment.GetEnvironmentVariable("TCS_UIA_READ_SECONDS"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? Math.Min(s, 8.0) : 3.0;
            idleStart = Stopwatch.GetTimestamp();
            Thread.Sleep(TimeSpan.FromSeconds(phaseSeconds));
            idleEnd = Stopwatch.GetTimestamp();
            _output.WriteLine($"read phase starts at {DateTime.Now:HH:mm:ss.fff}");

            readStart = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(readStart) < TimeSpan.FromSeconds(phaseSeconds))
            {
                foreach (string id in ReadIds)
                    _ = app.Text(id);
                reads++;
            }
            readEnd = Stopwatch.GetTimestamp();

            app.ExitNormally(TimeSpan.FromSeconds(15)).Should().BeTrue("出力トレースは正常終了時に書かれる");
        }

        string? events = null;
        for (int i = 0; i < 20 && events == null; i++)
        {
            events = Directory.GetFiles(traceDir, "events.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (events == null) Thread.Sleep(250);
        }
        events.Should().NotBeNull("出力トレースが書かれていない");

        var stages = File.ReadLines(events!)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(e => (Stage: e.GetProperty("stage").GetString() ?? "", Qpc: e.GetProperty("qpc").GetInt64(),
                Detail: e.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                ImageId: e.TryGetProperty("imageId", out JsonElement id) ? id.GetInt64() : 0))
            .ToList();
        Dictionary<string, int> Count(long from, long to)
        {
            var counts = stages
                .Where(e => e.Qpc >= from && e.Qpc < to)
                .GroupBy(e => e.Stage)
                .ToDictionary(g => g.Key, g => g.Count());
            // 合成が新しいソースフレームを採った tick（Ready かつ前回と違う番号）。
            long last = -1;
            int fresh = 0;
            foreach (var e in stages.Where(e => e.Stage == "compose.acquire" && e.Qpc < to))
            {
                bool isNew = e.Detail == "Ready" && e.ImageId > 0 && e.ImageId != last;
                if (isNew) last = e.ImageId;
                if (isNew && e.Qpc >= from) fresh++;
            }
            counts["compose.fresh"] = fresh;
            return counts;
        }
        Dictionary<string, int> idle = Count(idleStart, idleEnd);
        Dictionary<string, int> read = Count(readStart, readEnd);
        int Of(Dictionary<string, int> d, string stage) => d.TryGetValue(stage, out int n) ? n : 0;

        string summary = string.Create(CultureInfo.InvariantCulture,
            $"playing={playing} reads={reads} idle: position={Of(idle, "gst.position")} seek={Of(idle, "seek.issue")} " +
            $"seeking={Of(idle, "player.seeking")} sync={Of(idle, "sync.evaluate")} / read: position={Of(read, "gst.position")} " +
            $"seek={Of(read, "seek.issue")} seeking={Of(read, "player.seeking")} sync={Of(read, "sync.evaluate")}");
        summary += string.Create(CultureInfo.InvariantCulture,
            $" / compose idle={Of(idle, "compose.acquire")} read={Of(read, "compose.acquire")}" +
            $" fresh idle={Of(idle, "compose.fresh")} read={Of(read, "compose.fresh")}" +
            $" delivery idle={Of(idle, "gst.delivery")} read={Of(read, "gst.delivery")}");
        _output.WriteLine(summary);

        reads.Should().BeGreaterThan(10, summary);
        // 読み取り 1 回ごとに位置照会が起きていれば reads × 項目数だけ増える。読まない区間と同程度に収まること。
        Of(read, "gst.position").Should().BeLessThanOrEqualTo(Of(idle, "gst.position") * 5 / 4 + 10, summary);
        Of(read, "seek.issue").Should().Be(0, summary);
        Of(read, "player.seeking").Should().Be(0, summary);
        Of(read, "sync.evaluate").Should().Be(0, "LTC が無いので同期判定は走らない。 " + summary);

        // テスト 4: 読み取りで UI スレッドの低優先度の処理が遅れても（位置照会の回数が減るほど）、
        // 合成の tick・shim の配信・新しいフレームの採用は UI スレッドに依存せず続く。
        Of(read, "compose.acquire").Should().BeGreaterThanOrEqualTo(Of(idle, "compose.acquire") * 9 / 10, summary);
        if (playing)
        {
            Of(read, "gst.delivery").Should().BeGreaterThanOrEqualTo(Of(idle, "gst.delivery") * 9 / 10, summary);
            Of(read, "compose.fresh").Should().BeGreaterThanOrEqualTo(Of(idle, "compose.fresh") * 9 / 10, summary);
        }
    }
}
