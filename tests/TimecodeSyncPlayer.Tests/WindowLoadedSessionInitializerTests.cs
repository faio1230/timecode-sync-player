using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class WindowLoadedSessionInitializerTests
{
    [Fact]
    public void Initialize_RunsSessionStartupActionsInOrder_WhenInitializationSucceeds()
    {
        var calls = new List<string>();
        SpoutStartupState? appliedSpoutState = null;
        IntPtr mpv = new(100);
        var initializer = CreateInitializer(
            calls,
            initializeMpvSession: () =>
            {
                calls.Add("mpv-session");
                return new MpvSessionInitializationResult(true, mpv, MpvSessionInitializationFailure.None);
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
            "mpv-session",
            "assign-mpv:100",
            "audio-settings",
            "render-context",
            "spout",
            "spout-ui",
            "timer",
            "timeline");
        appliedSpoutState.Should().Be(new SpoutStartupState(true, "Spout ON"));
    }

    [Fact]
    public void Initialize_StopsAfterMpvCreateFailure()
    {
        var calls = new List<string>();
        WindowLoadedSessionInitializationError? error = null;
        var initializer = CreateInitializer(
            calls,
            initializeMpvSession: () => new MpvSessionInitializationResult(false, IntPtr.Zero, MpvSessionInitializationFailure.CreateFailed),
            showError: e =>
            {
                calls.Add($"error:{e}");
                error = e;
            });

        bool result = initializer.Initialize();

        result.Should().BeFalse();
        error.Should().Be(WindowLoadedSessionInitializationError.MpvCreateFailed);
        calls.Should().Equal("assign-mpv:0", "error:MpvCreateFailed");
    }

    [Fact]
    public void Initialize_StopsAfterMpvInitializeFailure()
    {
        var calls = new List<string>();
        WindowLoadedSessionInitializationError? error = null;
        var initializer = CreateInitializer(
            calls,
            initializeMpvSession: () => new MpvSessionInitializationResult(false, IntPtr.Zero, MpvSessionInitializationFailure.InitializeFailed),
            showError: e =>
            {
                calls.Add($"error:{e}");
                error = e;
            });

        bool result = initializer.Initialize();

        result.Should().BeFalse();
        error.Should().Be(WindowLoadedSessionInitializationError.MpvInitializeFailed);
        calls.Should().Equal("assign-mpv:0", "error:MpvInitializeFailed");
    }

    [Fact]
    public void Initialize_StopsAfterRenderContextFailure()
    {
        var calls = new List<string>();
        WindowLoadedSessionInitializationError? error = null;
        var initializer = CreateInitializer(
            calls,
            initializeMpvSession: () => new MpvSessionInitializationResult(true, new IntPtr(300), MpvSessionInitializationFailure.None),
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
        calls.Should().Equal("assign-mpv:300", "audio-settings", "render-context", "error:RenderContextCreateFailed");
    }

    private static WindowLoadedSessionInitializer CreateInitializer(
        List<string> calls,
        Func<MpvSessionInitializationResult>? initializeMpvSession = null,
        Action<IntPtr>? assignMpv = null,
        Action? applyAudioSettings = null,
        Func<bool>? createRenderContext = null,
        Func<SpoutStartupState>? initializeSpout = null,
        Action<SpoutStartupState>? applySpoutStartupState = null,
        Action? startTimer = null,
        Action? initializeTimeline = null,
        Action<WindowLoadedSessionInitializationError>? showError = null)
    {
        return new WindowLoadedSessionInitializer(
            initializeMpvSession ?? (() => new MpvSessionInitializationResult(true, IntPtr.Zero, MpvSessionInitializationFailure.None)),
            assignMpv ?? (mpv => calls.Add($"assign-mpv:{mpv.ToInt64()}")),
            applyAudioSettings ?? (() => calls.Add("audio-settings")),
            createRenderContext ?? (() => true),
            initializeSpout ?? (() => new SpoutStartupState(false, "Spout OFF")),
            applySpoutStartupState ?? (_ => calls.Add("spout-ui")),
            startTimer ?? (() => calls.Add("timer")),
            initializeTimeline ?? (() => calls.Add("timeline")),
            showError ?? (e => calls.Add($"error:{e}")));
    }
}
