using System.IO;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>
/// v0.6.0: proResGpu の未知の値の警告は起動時の 1 回だけ（作り直しでは読み直さない）。
/// Log.Logger を差し替えるため AppSettingsCompatibilityTests と同じ collection で直列実行する。
/// </summary>
[Collection("Project serializer state")]
public class GstBackendStateProResGpuLogTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task UnknownProResGpuValue_WarnsOnlyOnceAcrossRecreate()
    {
        // 並行の試験も同じ警告を出しうるので、値を一意にして数える。
        string unknown = "bogus-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(
            Path.GetTempPath(), "tcs-prores-gpu-log-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"proResGpu\":\"" + unknown + "\"}");
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        try
        {
            var manager = new AppSettingsManager(path);
            await manager.LoadAsync();
            var state = new GstBackendState(native, manager);

            state.EnsurePlayer().Should().BeTrue();
            state.RecreatePlayer(new IntPtr(0x99)).Should().BeTrue();
            state.RecreatePlayer(new IntPtr(0x9A)).Should().BeTrue();
        }
        finally
        {
            Log.Logger = previous;
            File.Delete(path);
        }

        native.SetProResGpuCalls.Should().Equal(
            GstNative.ProResGpuAuto, GstNative.ProResGpuAuto, GstNative.ProResGpuAuto);
        LogEvent[] events;
        lock (sink.Events) events = sink.Events.ToArray();
        events.Where(e => e.Level == LogEventLevel.Warning
                && e.Properties.TryGetValue("Value", out LogEventPropertyValue? v)
                && v is ScalarValue { Value: string s } && s == unknown)
            .Should().HaveCount(1);
    }
}
