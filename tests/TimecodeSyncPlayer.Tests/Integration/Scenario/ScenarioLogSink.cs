using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests.Integration;

internal sealed record ScenarioGateEvent(long AtMilliseconds, string Name, string Message);

/// <summary>
/// v0.5.4 C4: `sync.gate` の Debug 行を仮想時刻つきで拾う Serilog の sink。
/// 製品コードは変えない（グローバルの Log.Logger をテスト中だけ差し替える）。
/// 並列実行の別テストのログを混ぜないため、sink を作ったスレッドのログだけを拾う。
/// </summary>
internal sealed class ScenarioLogSink : ILogEventSink, IDisposable
{
    private readonly ScenarioClock? _clock;
    private readonly ILogger _previous;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<ScenarioGateEvent> _events = [];

    public ScenarioLogSink(ScenarioClock? clock = null)
    {
        _clock = clock;
        _previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(this)
            .CreateLogger();
    }

    public IReadOnlyList<ScenarioGateEvent> GateEvents
    {
        get { lock (_events) return _events.ToArray(); }
    }

    public void Emit(LogEvent logEvent)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            return;

        string message = logEvent.RenderMessage();
        if (!message.StartsWith("sync.gate ", StringComparison.Ordinal))
            return;

        string name = message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ElementAtOrDefault(1) ?? "";
        lock (_events)
            _events.Add(new ScenarioGateEvent(_clock?.MonotonicMilliseconds ?? 0, name, message));
    }

    public int Count(string name) => GateEvents.Count(e => e.Name == name);

    public void Dispose() => Log.Logger = _previous;
}
