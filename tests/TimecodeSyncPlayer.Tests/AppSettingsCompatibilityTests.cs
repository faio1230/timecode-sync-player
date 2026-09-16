using System.IO;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.3 設定ファイル互換（廃止した backend / outputBackend キー）。Log.Logger を差し替えるため
/// ProjectSerializerCanvasTests と同じ collection で直列実行する。
/// </summary>
[Collection("Project serializer state")]
public class AppSettingsCompatibilityTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task LoadAsync_LegacyBackendAndCpuOutputKeys_AreIgnoredWithOneWarning()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, """{"backend":0,"outputBackend":0,"volume":55}""");
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var manager = new AppSettingsManager(path);
            await manager.LoadAsync();

            manager.Current.OutputBackend.Should().Be(OutputBackend.Gpu);
            manager.Current.Volume.Should().Be(55);
            (await File.ReadAllTextAsync(path)).Should().Contain("\"outputBackend\":0",
                "設定ファイルは書き換えない");
        }
        finally
        {
            Log.Logger = previous;
            Directory.Delete(directory, recursive: true);
        }

        LogEvent[] events;
        lock (sink.Events) events = sink.Events.ToArray();
        events.Where(e => e.Level == LogEventLevel.Warning).Should().HaveCount(1);
    }

    [Fact]
    public async Task LoadAsync_ShippingConfigKeys_LoadWithoutWarning()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, """{"outputBackend":1,"volume":42}""");
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var manager = new AppSettingsManager(path);
            await manager.LoadAsync();

            manager.Current.OutputBackend.Should().Be(OutputBackend.Gpu);
            manager.Current.Volume.Should().Be(42);
        }
        finally
        {
            Log.Logger = previous;
            Directory.Delete(directory, recursive: true);
        }

        LogEvent[] events;
        lock (sink.Events) events = sink.Events.ToArray();
        events.Where(e => e.Level == LogEventLevel.Warning).Should().BeEmpty();
    }
}
