using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapEnterActionDispatcherTests
{
    [Fact]
    public void Execute_DispatchesEnterBlackGap()
    {
        var recorder = new Recorder();
        var dispatcher = new GapEnterActionDispatcher(CreateHandlers(recorder));

        dispatcher.Execute(new GapEnterAction(GapEnterActionType.EnterBlackGap), CreateResult());

        recorder.Calls.Should().Equal("black");
    }

    [Fact]
    public void Execute_DispatchesSeekToFinalFrameWithResultAndAction()
    {
        var recorder = new Recorder();
        var dispatcher = new GapEnterActionDispatcher(CreateHandlers(recorder));
        TimelineQueryResult result = CreateResult();
        var action = new GapEnterAction(GapEnterActionType.SeekToFinalFrame, TargetSeconds: 9.9);

        dispatcher.Execute(action, result);

        recorder.Calls.Should().Equal("seek:9.9:Previous");
    }

    [Fact]
    public void Execute_DispatchesLoadPreviousTrackWithRequiredValues()
    {
        var recorder = new Recorder();
        var dispatcher = new GapEnterActionDispatcher(CreateHandlers(recorder));
        var action = new GapEnterAction(
            GapEnterActionType.LoadPreviousTrack,
            TargetSeconds: 9.9,
            DurationSeconds: 10.0,
            Fps: 29.97);

        dispatcher.Execute(action, CreateResult());

        recorder.Calls.Should().Equal("load:Previous:9.9:10:29.97");
    }

    [Fact]
    public void Execute_IgnoresLoadPreviousTrack_WhenRequiredValuesAreMissing()
    {
        var recorder = new Recorder();
        var dispatcher = new GapEnterActionDispatcher(CreateHandlers(recorder));

        dispatcher.Execute(new GapEnterAction(GapEnterActionType.LoadPreviousTrack), CreateResult());

        recorder.Calls.Should().BeEmpty();
    }

    private static GapEnterActionHandlers CreateHandlers(Recorder recorder) => new(
        EnterBlackGap: () => recorder.Calls.Add("black"),
        ForceBlack: () => recorder.Calls.Add("force"),
        UseCachedFrame: () => recorder.Calls.Add("cached"),
        SeekToFinalFrame: (result, action) => recorder.Calls.Add($"seek:{action.TargetSeconds}:{result.PreviousTrack?.Name}"),
        LoadPreviousTrack: (track, target, duration, fps) => recorder.Calls.Add($"load:{track.Name}:{target}:{duration}:{fps}"));

    private static TimelineQueryResult CreateResult()
    {
        var previous = new PlaylistTrack(
            Guid.NewGuid(),
            @"C:\media\prev.mp4",
            "Previous",
            TimeSpan.Zero,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            null,
            true);
        return new TimelineQueryResult(TimelineQueryStatus.Gap, null, 0, previous);
    }

    private sealed class Recorder
    {
        public List<string> Calls { get; } = [];
    }
}
