using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public sealed class WindowLoadedSessionInitializerTests
{
    [Fact]
    public void Initialize_RunsSessionStartupActionsInOrder_WhenInitializationSucceeds()
    {
        var calls = new List<string>();
        SpoutStartupState? appliedSpoutState = null;
        var initializer = CreateInitializer(
            calls,
            initializePlayback: () =>
            {
                calls.Add("playback");
                return PlaybackResult.Ok;
            },
            createRenderContext: () =>
            {
                calls.Add("render-context");
                return true;
            },
            initializeSpout: () =>
            {
                calls.Add("spout");
                return new SpoutStartupState(IsButtonEnabled: true, ToggleLabel: "Spout ON");
            },
            applySpoutStartupState: state =>
            {
                calls.Add("spout-ui");
                appliedSpoutState = state;
            },
            applyAudioSettings: () => calls.Add("audio-settings"));

        bool result = initializer.Initialize();

        result.Should().BeTrue();
        calls.Should().Equal(
            "playback",
            "audio-settings",
            "render-context",
            "spout",
            "spout-ui",
            "timer",
            "timeline");
        appliedSpoutState.Should().Be(new SpoutStartupState(true, "Spout ON"));
    }

    [Fact]
    public void Initialize_StopsAfterPlaybackInitializeFailure()
    {
        var calls = new List<string>();
        WindowLoadedSessionInitializationError? error = null;
        var initializer = CreateInitializer(
            calls,
            initializePlayback: () => PlaybackResult.Fail("player create failed"),
            showError: e =>
            {
                calls.Add($"error:{e}");
                error = e;
            });

        bool result = initializer.Initialize();

        result.Should().BeFalse();
        error.Should().Be(WindowLoadedSessionInitializationError.PlaybackInitializeFailed);
        calls.Should().Equal("error:PlaybackInitializeFailed");
    }

    [Fact]
    public void Initialize_StopsAfterRenderContextFailure()
    {
        var calls = new List<string>();
        WindowLoadedSessionInitializationError? error = null;
        var initializer = CreateInitializer(
            calls,
            initializePlayback: () => PlaybackResult.Ok,
            createRenderContext: () =>
            {
                calls.Add("render-context");
                return false;
            },
            showError: e =>
            {
                calls.Add($"error:{e}");
                error = e;
            });

        bool result = initializer.Initialize();

        result.Should().BeFalse();
        error.Should().Be(WindowLoadedSessionInitializationError.RenderContextCreateFailed);
        calls.Should().Equal("audio-settings", "render-context", "error:RenderContextCreateFailed");
    }

    private static WindowLoadedSessionInitializer CreateInitializer(
        List<string> calls,
        Func<PlaybackResult>? initializePlayback = null,
        Action? applyAudioSettings = null,
        Func<bool>? createRenderContext = null,
        Func<SpoutStartupState>? initializeSpout = null,
        Action<SpoutStartupState>? applySpoutStartupState = null,
        Action? startTimer = null,
        Action? initializeTimeline = null,
        Action<WindowLoadedSessionInitializationError>? showError = null)
    {
        return new WindowLoadedSessionInitializer(
            initializePlayback ?? (() => PlaybackResult.Ok),
            applyAudioSettings ?? (() => calls.Add("audio-settings")),
            createRenderContext ?? (() => true),
            initializeSpout ?? (() => new SpoutStartupState(false, "Spout OFF")),
            applySpoutStartupState ?? (_ => calls.Add("spout-ui")),
            startTimer ?? (() => calls.Add("timer")),
            initializeTimeline ?? (() => calls.Add("timeline")),
            showError ?? (e => calls.Add($"error:{e}")));
    }
}
