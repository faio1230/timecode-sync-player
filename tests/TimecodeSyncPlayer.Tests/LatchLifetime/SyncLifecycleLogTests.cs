using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests.LatchLifetime;

/// <summary>
/// v0.5.2 段 1: できごとの列（"Sync lifecycle:" の行）。段 0 のできごとを起こす手順ごとに、出る
/// できごとを固定する。候補どうしでログの列を突き合わせる材料なので、出す場所と条件を変えたら
/// ここが赤になる。BeginFileLoad を通らない読み込みからは FileLoad を出さない（設計書 §3 段 1）。
/// </summary>
[Collection("Serilog global logger")]
public sealed class SyncLifecycleLogTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous;
        private readonly ListSink _sink = new();

        public LoggerCapture()
        {
            _previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_sink).CreateLogger();
        }

        /// <summary>"Sync lifecycle:" の行を "できごと/source" の列にする。</summary>
        public List<string> Lifecycle()
        {
            lock (_sink.Events)
                return _sink.Events
                    .Where(e => e.MessageTemplate.Text.StartsWith("Sync lifecycle:", StringComparison.Ordinal))
                    .Select(e => $"{Scalar(e, "Event")}/{Scalar(e, "Source")}")
                    .ToList();
        }

        public void Dispose() => Log.Logger = _previous;

        private static string Scalar(LogEvent e, string name) =>
            e.Properties.TryGetValue(name, out LogEventPropertyValue? v) && v is ScalarValue s
                ? s.Value?.ToString() ?? ""
                : "";
    }

    public static TheoryData<SyncMode, LifecycleEvent, string[]> Cases()
    {
        var data = new TheoryData<SyncMode, LifecycleEvent, string[]>();
        foreach (SyncMode mode in new[] { SyncMode.Continue, SyncMode.Single })
        {
            data.Add(mode, LifecycleEvent.FileLoad, ["FileLoad/load"]);
            data.Add(mode, LifecycleEvent.FileLoadWithoutBegin, []);
            data.Add(mode, LifecycleEvent.SyncEnabledOff, ["SyncDisabled/SyncEnabledChanged"]);
            data.Add(mode, LifecycleEvent.SyncEnabledOn, ["SyncEnabled/SyncEnabledChanged"]);
            // 3 行目: MonitorStopped の SetMonitoring(false) で MonitoringChanged に再入する。harness の
            // IsMonitoring は値が同じでも通知する（実アプリの VM は値が変わったときだけ通知するので 2 行）。
            data.Add(mode, LifecycleEvent.MonitoringStopped,
                ["MonitoringStopped/MonitoringChanged", "MonitorDeviceStopped/stopped", "MonitoringStopped/MonitoringChanged"]);
            data.Add(mode, LifecycleEvent.MonitoringStarted, ["MonitoringStarted/MonitoringChanged"]);
            data.Add(mode, LifecycleEvent.ManualSeek, ["ManualSeek/manual", "ManualSeek/manual"]);
            data.Add(mode, LifecycleEvent.StopPlayback, ["PlaybackStopped/PlaybackStopped"]);
            data.Add(mode, LifecycleEvent.SignalRecovered, ["SignalRecovered/valid-frames"]);
            data.Add(mode, LifecycleEvent.JumpRecovery, ["SignalRecovered/held-jump"]);
            data.Add(mode, LifecycleEvent.NormalFrame, []);
            data.Add(mode, LifecycleEvent.FpsModeChanged, ["FpsModeChanged/FpsModeChanged"]);
            data.Add(mode, LifecycleEvent.CorrectionModeChanged, ["CorrectionModeChanged/CorrectionModeChanged"]);
            data.Add(mode, LifecycleEvent.SignalLossModeChanged, ["SignalLossModeChanged/SignalLossModeChanged"]);
        }
        data.Add(SyncMode.Continue, LifecycleEvent.SyncModeChanged, ["SyncModeChanged/SyncModeChanged"]);
        data.Add(SyncMode.Single, LifecycleEvent.SyncModeChanged, ["SyncModeChanged/SyncModeChanged"]);
        data.Add(SyncMode.Continue, LifecycleEvent.GapEnter, ["GapEnter/EnteringFreeze"]);
        data.Add(SyncMode.Continue, LifecycleEvent.GapExit, ["GapExit/EnteringFreeze"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_event_procedure_records_the_expected_lifecycle_events(
        SyncMode mode, LifecycleEvent evt, string[] expected)
    {
        LatchLifetimeScenario s = mode == SyncMode.Continue
            ? LatchLifetimeScenario.Continue()
            : LatchLifetimeScenario.Single();
        s.Prepare(evt).Should().BeTrue("前提が作れること");

        using var capture = new LoggerCapture();
        s.Fire(evt);

        capture.Lifecycle().Should().Equal(expected);
    }

    [Fact]
    public void Continue_track_switch_records_a_file_load_from_the_switch()
    {
        LatchLifetimeScenario s = LatchLifetimeScenario.Continue();

        using var capture = new LoggerCapture();
        s.Frame(45.0);   // 2 本目 [40,70) へ切替

        capture.Lifecycle().Should().Equal("FileLoad/track-switch");
    }

    [Fact]
    public void Single_boundary_hold_release_records_an_event()
    {
        // ホールドの立て方は段 0 の ClipBoundaryHeld と同じ（範囲外の LTC で端へシークしてホールド）。
        LatchLifetimeScenario s = LatchArrangements.Arrange(LatchArrangements.ClipBoundaryHeld, SyncMode.Single);
        s.Read(LatchArrangements.ClipBoundaryHeld).Should().BeTrue("配置で境界ホールドが立っていること");

        using var capture = new LoggerCapture();
        s.Frame(15.0);   // LTC が clipOut=20 の内側へ戻り、端でのホールドが解除される

        capture.Lifecycle().Should().Equal("BoundaryHoldReleased/left-boundary");
    }
}
