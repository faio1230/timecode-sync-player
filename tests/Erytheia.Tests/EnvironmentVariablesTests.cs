using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Erytheia.Contracts;
using Erytheia.Output;

namespace Erytheia.Tests;

/// <summary>
/// v0.6.7: 環境変数の名前を ERYTHEIA_* に変え、v0.6.x の間は旧の名前（TIMECODE_SYNC_PLAYER_*・TCS_*）も読む。
/// Log.Logger を差し替えるため ProjectSerializerCanvasTests と同じ collection で直列実行する。
/// </summary>
[Collection("Project serializer state")]
public class EnvironmentVariablesTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Theory]
    [InlineData("TIMECODE_SYNC_PLAYER_SETTINGS_PATH", "ERYTHEIA_SETTINGS_PATH")]
    [InlineData("TIMECODE_SYNC_PLAYER_OUTPUT_TRACE", "ERYTHEIA_OUTPUT_TRACE")]
    [InlineData("TCS_LOG_FILE", "ERYTHEIA_LOG_FILE")]
    [InlineData("TCS_PUMP_BUDGET_MS", "ERYTHEIA_PUMP_BUDGET_MS")]
    public void ToNewName_ReplacesTheOldPrefix(string legacy, string expected)
    {
        EnvironmentVariables.ToNewName(legacy).Should().Be(expected);
        EnvironmentVariables.LegacyNameByName[expected].Should().Be(legacy);
    }

    [Fact]
    public void LegacyNames_MapToTheConstantsTheAppReads()
    {
        // 2 つの旧の系列が同じ新しい名前に重ならない（重なると辞書を作る所で落ちる）。
        EnvironmentVariables.LegacyNameByName.Keys.Should().Contain(new[]
        {
            AppSettingsManager.SettingsPathEnvironmentVariable,
            SpoutDefaults.SenderNameEnvironmentVariable,
            OutputTrace.EnvironmentVariable,
            OutputTrace.CapacityEnvironmentVariable,
            SeekTimeBudget.PumpBudgetEnvironmentVariable,
            SeekLatencyCompensator.EnvironmentVariable,
            GapPlayerModePolicy.EnvironmentVariable,
        });
        EnvironmentVariables.LegacyNameByName.Keys.Should().OnlyContain(name => name.StartsWith("ERYTHEIA_", StringComparison.Ordinal));
    }

    [Fact]
    public void Get_NewNameOnly_ReturnsItWithoutWarning()
    {
        LogEvent[] events = Capture(loggerReady: true, () =>
            EnvironmentVariables.Get("ERYTHEIA_GAP_MODE", Env(("ERYTHEIA_GAP_MODE", "black"))).Should().Be("black"));

        events.Should().BeEmpty();
    }

    [Fact]
    public void Get_OldNameOnly_ReturnsItAndWarnsOncePerVariable()
    {
        var env = Env(("TCS_GAP_MODE", "black"));
        LogEvent[] events = Capture(loggerReady: true, () =>
        {
            EnvironmentVariables.Get("ERYTHEIA_GAP_MODE", env).Should().Be("black");
            EnvironmentVariables.Get("ERYTHEIA_GAP_MODE", env).Should().Be("black");
        });

        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogEventLevel.Warning);
        events[0].RenderMessage().Should().Contain("TCS_GAP_MODE").And.Contain("ERYTHEIA_GAP_MODE");
    }

    [Fact]
    public void Get_BothNames_UsesTheNewOneAndWarnsOnce()
    {
        var env = Env(("ERYTHEIA_GAP_MODE", "new"), ("TCS_GAP_MODE", "old"));
        LogEvent[] events = Capture(loggerReady: true, () =>
        {
            EnvironmentVariables.Get("ERYTHEIA_GAP_MODE", env).Should().Be("new");
            EnvironmentVariables.Get("ERYTHEIA_GAP_MODE", env).Should().Be("new");
        });

        events.Should().ContainSingle();
        events[0].RenderMessage().Should().Contain("両方");
    }

    [Fact]
    public void Get_NameWithoutOldName_IsReadAsIs()
    {
        LogEvent[] events = Capture(loggerReady: true, () =>
            EnvironmentVariables.Get("PATH", Env(("PATH", "x"))).Should().Be("x"));

        events.Should().BeEmpty();
    }

    [Fact]
    public void Get_BeforeTheLogger_WarnsWhenFlushed()
    {
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        EnvironmentVariables.ResetForTesting(loggerReady: false);
        try
        {
            EnvironmentVariables.Get("ERYTHEIA_LOG_FILE", Env(("TCS_LOG_FILE", "x.log"))).Should().Be("x.log");
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
            EnvironmentVariables.FlushPendingWarnings();
        }
        finally
        {
            Log.Logger = previous;
            EnvironmentVariables.ResetForTesting(loggerReady: true);
        }

        LogEvent[] events;
        lock (sink.Events) events = sink.Events.Where(IsOurs).ToArray();
        events.Should().ContainSingle();
        events[0].RenderMessage().Should().Contain("TCS_LOG_FILE");
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private static LogEvent[] Capture(bool loggerReady, Action action)
    {
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        EnvironmentVariables.ResetForTesting(loggerReady);
        try
        {
            action();
        }
        finally
        {
            Log.Logger = previous;
            EnvironmentVariables.ResetForTesting(loggerReady: true);
        }

        lock (sink.Events) return sink.Events.Where(IsOurs).ToArray();
    }

    /// <summary>Log.Logger はプロセス全体で共有されるため、この試験の行だけに絞る。</summary>
    private static bool IsOurs(LogEvent logEvent) =>
        logEvent.RenderMessage().Contains("旧の名前", StringComparison.Ordinal);
}
