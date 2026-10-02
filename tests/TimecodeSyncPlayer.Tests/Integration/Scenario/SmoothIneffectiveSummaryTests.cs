using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.4 段 3（設計書 3-3）: Smooth の「効いていない」の検出の発火数を Sync hold summary の行末に
/// smoothIneffective= として出す（配布ビルドの一式で数えるため）。既存の項目の順と形は変えず、
/// 最後の otherBackwardWhileStopped の後ろに足す。数の増え方は SyncCorrectionControllerTests。
/// </summary>
[Collection("Serilog global logger")]
public class SmoothIneffectiveSummaryTests
{
    private sealed class SummarySink : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        public List<string> Lines { get; } = new();
        public SummarySink() => Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(this).CreateLogger();
        public void Emit(LogEvent e)
        {
            if (Environment.CurrentManagedThreadId == _owner &&
                e.MessageTemplate.Text.StartsWith("Sync hold summary", StringComparison.Ordinal))
                lock (Lines) Lines.Add(e.RenderMessage());
        }
        public void Dispose() => Log.Logger = _previous;
    }

    [Fact]
    public void SyncHoldSummary_EndsWithSmoothIneffective()
    {
        using var sink = new SummarySink();
        var h = new SyncScenarioHarness(enableCorrection: true);
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.IsMonitoring = false;

        string line;
        lock (sink.Lines) line = sink.Lines.Single();
        Match m = Regex.Match(line, @"\botherBackwardWhileStopped=\d+ smoothIneffective=(\d+)$");
        m.Success.Should().BeTrue("smoothIneffective は行末（otherBackwardWhileStopped の後ろ）に足す: " + line);
        long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(0, "発火していなければ 0");
    }
}
