using System.IO;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4: 資源が尽きた条件（ディスクの空き 0.6 GB）で、出力トレースの保存の中から OutOfMemoryException が
/// 外へ出てアプリが落ちた（GPU worker の終了処理の OutputEngine から）。診断は挙動を変えないので、保存は
/// どんな例外でも出力を諦めて破棄し、警告を 1 行出して戻る。
/// </summary>
[Collection("Serilog global logger")]
public class OutputTraceSaveFailureTests
{
    /// <summary>メモリが尽きた状態を模す: 警告を受け取った記録を残してから OutOfMemoryException を投げる。</summary>
    private sealed class OutOfMemorySink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            lock (Events) Events.Add(logEvent);
            throw new OutOfMemoryException("模擬: 警告の書き出しでメモリが尽きた");
        }
    }

    [Fact]
    public void Save_WhenWritingAndItsWarningBothFail_DoesNotThrow_DiscardsTheEvents()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-save", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 既に manifest.json があるので、保存の書き出し（CreateNew）が失敗する。
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        var trace = new OutputTrace(dir, 10);
        trace.Add("compose.publish", "GPU");
        trace.Add("compose.publish", "GPU");

        var sink = new OutOfMemorySink();
        ILogger previous = Log.Logger;
        // AuditTo の sink は例外を呼び出し側へ返す（警告の書き出しそのものが失敗する形）。
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().AuditTo.Sink(sink).CreateLogger();
        try
        {
            Action save = () => trace.Save(
                new OutputTraceRunSummary("completed", true, false, 1920, 1080, 0, 0, "test", 0, 0, 0, null, false, null),
                new LatestPool(3), null);

            save.Should().NotThrow("記録の経路は製品（GPU worker の終了処理）を落とさない");
        }
        finally
        {
            Log.Logger = previous;
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }

        trace.Snapshot().Should().BeEmpty("保存を諦めたトレースは破棄する");
        LogEvent[] events;
        lock (sink.Events) events = sink.Events.ToArray();
        events.Should().ContainSingle(e => e.Level == LogEventLevel.Warning, "警告は 1 行だけ試みる");
    }
}
