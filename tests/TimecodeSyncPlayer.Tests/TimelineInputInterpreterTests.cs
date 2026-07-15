using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimelineInputInterpreterTests
{
    [Fact]
    public void ZoomIn_InvokesZoomThenRangeUpdate()
    {
        var calls = new List<string>();
        var interpreter = CreateInterpreter(calls, zoomIn: () => calls.Add("zoom-in"));

        interpreter.ZoomIn();

        calls.Should().Equal("zoom-in", "update-ranges");
    }

    [Fact]
    public void ZoomOut_InvokesZoomThenRangeUpdate()
    {
        var calls = new List<string>();
        var interpreter = CreateInterpreter(calls, zoomOut: () => calls.Add("zoom-out"));

        interpreter.ZoomOut();

        calls.Should().Equal("zoom-out", "update-ranges");
    }

    [Fact]
    public void ScrollHorizontal_ConvertsAbsoluteNewValueToDeltaThenUpdatesRanges()
    {
        var calls = new List<string>();
        double? delta = null;
        var interpreter = CreateInterpreter(
            calls,
            getHorizontal: () => 12.5,
            scrollHorizontal: value => { delta = value; calls.Add("scroll-horizontal"); });

        interpreter.ScrollHorizontal(20.25);

        delta.Should().Be(7.75);
        calls.Should().Equal("scroll-horizontal", "update-ranges");
    }

    [Fact]
    public void ScrollHorizontal_WithoutDrawingStateReturnsWithoutEffects()
    {
        var calls = new List<string>();
        var interpreter = CreateInterpreter(calls, getHorizontal: () => null);

        interpreter.ScrollHorizontal(20);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void ScrollVertical_TruncatesNewValueBeforeSubtractingCurrentOffset()
    {
        var calls = new List<string>();
        int? delta = null;
        var interpreter = CreateInterpreter(
            calls,
            getVertical: () => 3,
            scrollVertical: value => { delta = value; calls.Add("scroll-vertical"); });

        interpreter.ScrollVertical(8.9);

        delta.Should().Be(5);
        calls.Should().Equal("scroll-vertical", "update-ranges");
    }

    [Fact]
    public void ScrollVertical_WithoutDrawingStateReturnsWithoutEffects()
    {
        var calls = new List<string>();
        var interpreter = CreateInterpreter(calls, getVertical: () => null);

        interpreter.ScrollVertical(4);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void RequestSeek_ForwardsSameEventArgsInstance()
    {
        var calls = new List<string>();
        TimelineSeekEventArgs? forwarded = null;
        var interpreter = CreateInterpreter(
            calls,
            requestSeek: args => { forwarded = args; calls.Add("seek"); });
        var expected = new TimelineSeekEventArgs(123.456, 2);

        interpreter.RequestSeek(expected);

        forwarded.Should().BeSameAs(expected);
        calls.Should().Equal("seek");
    }

    private static TimelineInputInterpreter CreateInterpreter(
        List<string> calls,
        Action? zoomIn = null,
        Action? zoomOut = null,
        Func<double?>? getHorizontal = null,
        Action<double>? scrollHorizontal = null,
        Func<int?>? getVertical = null,
        Action<int>? scrollVertical = null,
        Action<TimelineSeekEventArgs>? requestSeek = null)
    {
        return new TimelineInputInterpreter(new TimelineInputEffects(
            ZoomIn: zoomIn ?? (() => calls.Add("zoom-in")),
            ZoomOut: zoomOut ?? (() => calls.Add("zoom-out")),
            GetHorizontalScrollSeconds: getHorizontal ?? (() => 0),
            ScrollHorizontal: scrollHorizontal ?? (_ => calls.Add("scroll-horizontal")),
            GetVerticalScrollOffset: getVertical ?? (() => 0),
            ScrollVertical: scrollVertical ?? (_ => calls.Add("scroll-vertical")),
            UpdateScrollBarRanges: () => calls.Add("update-ranges"),
            RequestSeek: requestSeek ?? (_ => calls.Add("seek"))));
    }
}
