using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.3 段 4（設計書 3 節の B）: ロード解除の再適用（D20-b (i)・D27-b: 保持の Duplicate では通常の同期が走らないので、
/// 解除の後に最後に受理した値を 1 回だけ適用する）を、期限 1.5 秒（D35、88c36d1）でなく規則 4 で決める。
/// マスターが動いた（Normal のフレーム）なら通常の同期（規則 2・3）が位置を合わせるので、回収待ちの解除は捨てる。
/// マスターが止まっている（解除から保持まで Normal が無い）なら、保持で 1 回だけ回収する（停止した値への 1 回の合わせ）。
/// D35 が塞いだ型: ロードの後に Normal が続いた数秒後、保持の始まり（Reverse・Duplicate）で古い解除の再適用が走る。
/// </summary>
[Collection("Serilog global logger")]
public class FileLoadReleaseRule4Tests
{
    private const long BaseMilliseconds = 50_000;
    private const string ReapplyLine = "Timecode sync: reapplying the last accepted timecode once after file load";

    private readonly ITestOutputHelper _output;

    public FileLoadReleaseRule4Tests(ITestOutputHelper output) => _output = output;

    private sealed class LineSink : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly Func<long> _now;
        public List<string> Lines { get; } = new();

        public LineSink(Func<long> now)
        {
            _now = now;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(this).CreateLogger();
        }

        public void Emit(LogEvent logEvent)
        {
            if (Environment.CurrentManagedThreadId != _owner)
                return;
            string message = logEvent.RenderMessage();
            if (message.Contains("after file load", StringComparison.Ordinal) ||
                message.Contains("file load release", StringComparison.Ordinal) ||
                message.StartsWith("File load landing", StringComparison.Ordinal) ||
                message.StartsWith("Continue mode: switching", StringComparison.Ordinal))
                lock (Lines) Lines.Add($"{_now()}ms {message}");
        }

        public int Count(string prefix)
        {
            lock (Lines) return Lines.Count(l => l.Contains(prefix, StringComparison.Ordinal));
        }

        public void Dispose() => Log.Logger = _previous;
    }

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange(LtcSignalLossMode lossMode)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
        };
        h.AddTrack("A", 0, 60);
        h.ManualPlay();
        h.AdvancePlayback(5.0);
        return (h, clock);
    }

    private void Play(SyncScenarioHarness h, ScenarioClock clock, LineSink sink)
    {
        long end = h.Ltc.NextMilliseconds + 400;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);
        foreach (string line in sink.Lines)
            _output.WriteLine(line);
    }

    /// <summary>
    /// D35 の型（短い版）: ロードの後に Normal が 1.0 秒続き（期限 1.5 秒の内）、その後に保持へ入る。マスターは動いていたので、
    /// 保持の始まりで解除の再適用を出さない（今のコードは期限の内なので再適用する）。
    /// </summary>
    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough)]
    [InlineData(LtcSignalLossMode.Stop)]
    public void NormalFramesAfterTheRelease_DiscardTheReapply_EvenWithinOnePointFiveSeconds(LtcSignalLossMode lossMode)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode);
        long start = clock.MonotonicMilliseconds;
        using var sink = new LineSink(() => clock.MonotonicMilliseconds - start);
        h.Ltc.Normal(5.0, TimeSpan.FromSeconds(1.0));       // 読み込み → 解除 → Normal が 1.0 秒
        h.Ltc.Duplicate(6.0, TimeSpan.FromSeconds(1.0));    // 保持
        Play(h, clock, sink);

        sink.Count("File load landing").Should().BeGreaterThanOrEqualTo(1, "前提: 読み込んで解除された");
        sink.Count(ReapplyLine).Should().Be(0,
            "解除の後にマスターが動いた（Normal）なら、保持の始まりで解除の再適用を出さない（規則 4）");
    }

    /// <summary>D35 の元の型（検証機の R-1、ロードの 6.4 秒後）: Normal が 6 秒続いた後の Reverse 1 枚で再適用しない（今も緑）。</summary>
    [Fact]
    public void NormalFramesForSixSeconds_ThenAReverseFrame_DoNotReapply()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.Stop);
        long start = clock.MonotonicMilliseconds;
        using var sink = new LineSink(() => clock.MonotonicMilliseconds - start);
        h.Ltc.Normal(5.0, TimeSpan.FromSeconds(6.0));
        h.Ltc.Jump(10.92);                                  // 2 フレーム戻った 1 枚（保持の始まりの Reverse）
        h.Ltc.Duplicate(11.0, TimeSpan.FromSeconds(1.0));
        Play(h, clock, sink);

        sink.Count(ReapplyLine).Should().Be(0, "ロードの数秒後の保持の始まりで、古い解除を再適用しない（D35）");
    }

    /// <summary>
    /// 規則 4: 解除の後に Normal が無いまま（無音 2 秒）保持の Duplicate が届いたら、マスターは止まっているので 1 回だけ回収して
    /// 再適用する（今のコードは期限 1.5 秒を過ぎたので捨てる）。
    /// </summary>
    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough)]
    [InlineData(LtcSignalLossMode.Stop)]
    public void HeldAfterSilence_WithoutNormalFrames_ReappliesExactlyOnce(LtcSignalLossMode lossMode)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode);
        long start = clock.MonotonicMilliseconds;
        using var sink = new LineSink(() => clock.MonotonicMilliseconds - start);
        h.Ltc.Normal(5.0, TimeSpan.FromMilliseconds(40));   // 1 枚で読み込み
        h.Ltc.Silence(TimeSpan.FromSeconds(2.0));
        h.Ltc.Duplicate(5.0, TimeSpan.FromSeconds(1.0));    // 止まった値で保持
        Play(h, clock, sink);

        sink.Count("File load landing").Should().BeGreaterThanOrEqualTo(1, "前提: 読み込んで解除された");
        sink.Count(ReapplyLine).Should().Be(1,
            "解除から保持まで マスターが動いていないので、保持で 1 回だけ再適用する（規則 4、期限は使わない）");
    }
}
