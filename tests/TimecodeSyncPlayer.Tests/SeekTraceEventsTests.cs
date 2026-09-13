using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// V3 計測イベント（seek.decide / seek.issue / seek.return）が events.jsonl に載ることを確認する。
/// 共有参照 OutputTrace.Current を使うため、対象イベントだけを値で絞って検証する。
/// </summary>
public class SeekTraceEventsTests
{
    private static List<JsonElement> Capture(Action action)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-seek-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trace = new OutputTrace(dir, capacity: 1000) { OriginQpc = Stopwatch.GetTimestamp() };
            OutputTrace.Current = trace;
            try
            {
                action();
            }
            finally
            {
                OutputTrace.Current = OutputTrace.Disabled;
            }

            trace.Save(
                new OutputTraceRunSummary("completed", false, false, 16, 16, 3, 3, "test", 0, 0, 0, null, false, null),
                new LatestPool(3),
                null);
            return File.ReadAllLines(Path.Combine(dir, "events.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }

    private static SyncPlaybackState SeekYieldingState(double playbackSeconds) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    private static IEnumerable<JsonElement> Events(List<JsonElement> events, string stage) =>
        events.Where(e => e.GetProperty("stage").GetString() == stage);

    [Fact]
    public void Decide_Seek_RecordsSeekDecideWithTargetMicroseconds()
    {
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(77.25, SeekYieldingState(0.0)));

        var decides = Events(events, "seek.decide").ToList();
        decides.Should().Contain(e => e.GetProperty("value").GetInt64() == 77_250_000);
    }

    [Fact]
    public void Decide_None_DoesNotRecordSeekDecide()
    {
        // |delta| = 0.1 秒 < tolerance 0.2 秒（6 フレーム @30fps）で None。
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(33.187, SeekYieldingState(33.287)));

        Events(events, "seek.decide")
            .Should().NotContain(e => e.GetProperty("value").GetInt64() == 33_187_000);
    }

    [Fact]
    public void SeekTo_RecordsIssueAndReturnAroundCommand()
    {
        var commands = new List<string>();
        List<JsonElement> events = Capture(() =>
        {
            var coordinator = new PlaybackOperationsCoordinator(
                new PlaybackControlState(),
                Effects(command => { commands.Add(command); return 0; }));
            coordinator.SeekTo(55.125).Should().BeTrue();
        });

        commands.Should().ContainSingle().Which.Should().Be("no-osd seek 55.125 absolute+exact");
        var issues = Events(events, "seek.issue").ToList();
        var returns = Events(events, "seek.return").ToList();
        issues.Should().Contain(e => e.GetProperty("value").GetInt64() == 55_125_000);
        returns.Should().NotBeEmpty();
        returns.First().GetProperty("qpc").GetInt64()
            .Should().BeGreaterThanOrEqualTo(issues.First().GetProperty("qpc").GetInt64());
    }

    [Fact]
    public void LoadFile_RecordsIssueAndReturnWithStartMicroseconds()
    {
        var commands = new List<string>();
        List<JsonElement> events = Capture(() =>
        {
            var coordinator = new PlaybackOperationsCoordinator(
                new PlaybackControlState(),
                Effects(command => { commands.Add(command); return 0; }));
            coordinator.LoadFile("C:\\media\\clip.mp4", 12.5).Should().BeTrue();
        });

        commands.Should().ContainSingle().Which.Should().Contain("loadfile");
        Events(events, "load.issue").Should().Contain(e => e.GetProperty("value").GetInt64() == 12_500_000);
        Events(events, "load.return").Should().NotBeEmpty();
    }

    [Fact]
    public void SeekTo_WhenCommandThrows_StillRecordsReturn()
    {
        List<JsonElement> events = Capture(() =>
        {
            var coordinator = new PlaybackOperationsCoordinator(
                new PlaybackControlState(),
                Effects(_ => throw new InvalidOperationException("test")));
            coordinator.SeekTo(9.5).Should().BeFalse();
        });

        Events(events, "seek.issue").Should().Contain(e => e.GetProperty("value").GetInt64() == 9_500_000);
        Events(events, "seek.return").Should().NotBeEmpty();
    }

    private static PlaybackOperationsEffects Effects(Func<string, int> commandString) => new(
        IsMpvReady: () => true,
        CommandString: commandString,
        SetPropertyString: (_, _) => 0,
        ResetPlayerStateForNewTrack: () => { },
        ResetVideoWidth: () => { },
        ResetVideoHeight: () => { },
        ClearLoadedTrackId: () => { },
        HasTimelinePanel: () => false,
        ClearTimelineLoadedTrackId: () => { },
        SetSeekBarValueFromPlayer: _ => { },
        SetTimeLabel: _ => { },
        SetPlayPauseIcon: _ => { },
        ResetGapFreezeAll: () => { },
        ResetGapFreeze: () => { },
        ClearGapFreezeFrame: () => { });
}
