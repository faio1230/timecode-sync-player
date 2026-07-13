using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class GapPlaybackCommandExecutorTests
{
    private static readonly IntPtr s_mpv = new(123);

    [Fact]
    public void PauseForGap_SetsPauseYesAndHidesOsdBar()
    {
        var api = new FakeMpvApi();
        var executor = new GapPlaybackCommandExecutor(api);

        GapPlaybackCommandResult result = executor.PauseForGap(s_mpv);

        result.PauseRc.Should().Be(0);
        result.OsdBarRc.Should().Be(0);
        api.SetProperties.Should().Equal(
            ("pause", "yes"),
            ("osd-bar", "no"));
    }

    [Fact]
    public void LoadPausedAt_LoadsFileAtTargetAndPausesPlayback()
    {
        var api = new FakeMpvApi();
        var executor = new GapPlaybackCommandExecutor(api);

        GapLoadCommandResult result = executor.LoadPausedAt(s_mpv, @"C:\media\track.mp4", 12.345);

        result.LoadRc.Should().Be(0);
        result.PauseRc.Should().Be(0);
        api.Commands.Should().ContainSingle()
            .Which.Should().Contain("loadfile");
        api.Commands[0].Should().Contain("start=12.345");
        api.SetProperties.Should().Equal(("pause", "yes"));
    }

    private sealed class FakeMpvApi : IMpvApi
    {
        public List<(string Name, string Value)> SetProperties { get; } = [];
        public List<string> Commands { get; } = [];

        public IntPtr Create() => s_mpv;
        public int Initialize(IntPtr ctx) => 0;
        public void TerminateDestroy(IntPtr ctx) { }
        public int SetPropertyString(IntPtr ctx, string name, string value)
        {
            SetProperties.Add((name, value));
            return 0;
        }

        public int GetProperty(IntPtr ctx, string name, int format, out double result)
        {
            result = 0;
            return -1;
        }

        public string GetPropertyString(IntPtr ctx, string name) => "";
        public int CommandString(IntPtr ctx, string args)
        {
            Commands.Add(args);
            return 0;
        }
        public void Free(IntPtr data) { }
        public int FormatDouble => 4;
    }
}
