using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 B6-24: Jump 補正のシーク連鎖の歯止めは、振る舞いの門（連続 3 回で止める）をやめ、
/// 計数と警告のログへ格下げする。シークは続き、警告はエピソード（1 秒以上しきい値の内側に
/// 留まって数え直すまで）に 1 回だけ出る。
/// </summary>
[Collection("Serilog global logger")]
public class B6JumpChainTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous;

        public LoggerCapture(ListSink sink)
        {
            Sink = sink;
            _previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        }

        public ListSink Sink { get; }

        public List<LogEvent> Warnings() =>
            Snapshot().Where(e => e.Level == LogEventLevel.Warning).ToList();

        private List<LogEvent> Snapshot()
        {
            lock (Sink.Events) return Sink.Events.ToList();
        }

        public void Dispose() => Log.Logger = _previous;
    }

    private static SyncCorrectionDecision Evaluate(
        SyncCorrectionController controller, double residualSeconds, double secondsAfterStart)
        => controller.Evaluate(
            residualSeconds, targetSeconds: 0.0, SyncCorrectionMode.Jump, smoothAvailable: true,
            T0.AddSeconds(secondsAfterStart), deadbandSeconds: 1.0 / 25.0);

    [Fact]
    public void JumpChain_AfterTheConsecutiveLimit_KeepsSeeking()
    {
        var controller = new SyncCorrectionController();

        // 旧: 連続 3 回で Idle（jump-limit）。B6-24: 止めず、計数と警告だけになる。
        for (int i = 0; i <= 5; i++)
        {
            Evaluate(controller, 0.100, i * 0.1).Action.Should().Be(
                SyncCorrectionActionType.Seek, "上限に達してもシークは続く（門 24 の格下げ）");
        }
    }

    [Fact]
    public void JumpChain_WarnsOncePerEpisode_AndRearmsAfterASettle()
    {
        var sink = new ListSink();
        using var capture = new LoggerCapture(sink);
        var controller = new SyncCorrectionController();

        for (int i = 0; i < 5; i++)
            Evaluate(controller, 0.100, i * 0.1);

        capture.Warnings().Should().ContainSingle(
            "連続の計数が上限（3）に達したときに 1 回だけ警告する")
            .Which.MessageTemplate.Text.Should().Contain("Jump correction chain");

        // 1 秒以上しきい値の内側に留まると数え直し、次の連鎖でまた 1 回警告する。
        Evaluate(controller, 0.010, 0.6);
        Evaluate(controller, 0.010, 1.7);
        for (int i = 0; i < 3; i++)
            Evaluate(controller, 0.100, 1.8 + (i * 0.1));

        capture.Warnings().Should().HaveCount(2, "セトルで数え直した後の連鎖でも警告する");
    }
}
