using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// V3 計測イベント（seek.decide / seek.issue / seek.return）が events.jsonl に載ることを確認する。
/// 共有参照 OutputTrace.Current を使うため、対象イベントだけを値で絞って検証する。
/// このクラスは OutputTrace コレクションに属し、同じ静的を差し替えるテストと並列に走らない。
/// </summary>
[Collection("OutputTrace")]
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
    public void Decide_SeekWithLearnedCompensation_RecordsCompensatedTarget()
    {
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.MarkSeekDecision(1_000);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(1_000 + (long)(0.2 * Stopwatch.Frequency), generation: 1, sourceSequence: 1); // L = 0.2
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), compensator);

        List<JsonElement> events = Capture(() => engine.Decide(77.25, SeekYieldingState(0.0)));

        // seek.issue は補償後のターゲットで出るため、seek.decide の value も補償後で揃える。
        Events(events, "seek.decide").Should().ContainSingle()
            .Which.GetProperty("value").GetInt64().Should().Be(77_450_000);
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

    private static string EvaluateDetail(List<JsonElement> events) =>
        Events(events, "sync.evaluate").Should().ContainSingle().Subject
            .GetProperty("detail").GetString()!;

    [Theory]
    [InlineData(false, true, false, "disabled")]
    [InlineData(true, false, false, "no-track")]
    [InlineData(true, true, true, "seeking")]
    public void Evaluate_EarlyNone_RecordsReasonAndLtc(bool syncEnabled, bool hasTrack, bool isSeeking, string reason)
    {
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(10.0, new SyncPlaybackState(
                SyncEnabled: syncEnabled,
                HasCurrentTrack: hasTrack,
                IsSeeking: isSeeking,
                PlaybackSeconds: 0.0,
                DurationSeconds: 200.0,
                VideoFps: 30.0,
                TimecodeFps: 30.0)));

        var evaluate = Events(events, "sync.evaluate").Should().ContainSingle().Subject;
        evaluate.GetProperty("value").GetInt64().Should().Be(10_000_000);
        evaluate.GetProperty("detail").GetString().Should().Contain($"reason={reason}");
        Events(events, "seek.decide").Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NotFinite_RecordsReasonWithoutValue()
    {
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(double.NaN, SeekYieldingState(0.0)));

        var evaluate = Events(events, "sync.evaluate").Should().ContainSingle().Subject;
        evaluate.GetProperty("value").GetInt64().Should().Be(0);
        evaluate.GetProperty("detail").GetString().Should().Contain("reason=not-finite");
    }

    [Fact]
    public void Evaluate_BadDuration_RecordsReason()
    {
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(1.0, new SyncPlaybackState(
                SyncEnabled: true,
                HasCurrentTrack: true,
                IsSeeking: false,
                PlaybackSeconds: 0.0,
                DurationSeconds: 0.0,
                VideoFps: 30.0,
                TimecodeFps: 30.0)));

        EvaluateDetail(events).Should().Contain("reason=bad-duration");
        Events(events, "seek.decide").Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_WithinTolerance_RecordsDeltaAndReason()
    {
        // 30fps × tolerance 6 フレーム = 0.2 秒。delta = -0.1 秒は許容内。
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(33.1, SeekYieldingState(33.2)));

        string detail = EvaluateDetail(events);
        detail.Should().Contain("delta=-0.100000");
        detail.Should().Contain("reason=within-tolerance");
        Events(events, "seek.decide").Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_Seek_RecordsDeltaAndReason()
    {
        List<JsonElement> events = Capture(() =>
            new SyncDecisionEngine().Decide(77.25, SeekYieldingState(0.0)));

        string detail = EvaluateDetail(events);
        detail.Should().Contain("delta=77.250000");
        detail.Should().Contain("reason=seek");
        Events(events, "seek.decide").Should().ContainSingle();
    }

    [Fact]
    public void SeekTo_RecordsIssueAndReturnAroundCommand()
    {
        var seeks = new List<double>();
        List<JsonElement> events = Capture(() =>
        {
            var coordinator = new PlaybackOperationsCoordinator(
                new PlaybackControlState(),
                Effects(seek: seconds => { seeks.Add(seconds); return PlaybackResult.Ok; }));
            coordinator.SeekTo(55.125).Should().BeTrue();
        });

        seeks.Should().ContainSingle().Which.Should().Be(55.125);
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
        var loads = new List<(string Path, double? Start, bool Paused)>();
        List<JsonElement> events = Capture(() =>
        {
            var playback = new PlaybackControlState();
            playback.SetPaused(false);
            var coordinator = new PlaybackOperationsCoordinator(
                playback,
                Effects(load: (path, start, paused) =>
                {
                    loads.Add((path, start, paused));
                    return PlaybackResult.Ok;
                }));
            coordinator.LoadFile("C:\\media\\clip.mp4", 12.5).Should().BeTrue();
        });

        loads.Should().ContainSingle()
            .Which.Should().Be(("C:\\media\\clip.mp4", (double?)12.5, false));
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
                Effects(seek: _ => throw new InvalidOperationException("test")));
            coordinator.SeekTo(9.5).Should().BeFalse();
        });

        Events(events, "seek.issue").Should().Contain(e => e.GetProperty("value").GetInt64() == 9_500_000);
        Events(events, "seek.return").Should().NotBeEmpty();
    }

    private static PlaybackOperationsEffects Effects(
        Func<double, PlaybackResult>? seek = null,
        Func<string, double?, bool, PlaybackResult>? load = null) => new(
        IsMpvReady: () => true,
        Load: load ?? ((_, _, _) => PlaybackResult.Ok),
        Seek: seek ?? (_ => PlaybackResult.Ok),
        Stop: () => PlaybackResult.Ok,
        SetPaused: _ => PlaybackResult.Ok,
        ResetPlayerStateForNewTrack: () => { },
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
