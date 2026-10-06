using System.Diagnostics;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.5 B3「LTC のフレームの検査」の数え（設計書 docs/design/v0.6.5-small-fixes.md 4 節）。
/// <see cref="LtcDecoder"/> が LTC のフレームを 1 つ読み終えるたびに 1 回だけ記録する。
/// デコーダは LTC の監視の開始ごとに作り直すので、起動からの累計はここ（<see cref="Shared"/>）に持つ。
/// 書くのはオーディオのスレッド、読むのは終了の手順（UI スレッド）なので Interlocked で数える。
/// </summary>
internal sealed class LtcFrameCheckStats
{
    /// <summary>アプリの LTC の監視が使う、起動からの累計。</summary>
    internal static LtcFrameCheckStats Shared { get; } = new();

    /// <summary>落とした理由。Debug の行の間引き（同じ理由は 1 秒に 1 行）の単位。</summary>
    internal enum RejectReason
    {
        /// <summary>前の同期ワードが無い（受け始め・無音の後）ので長さを確かめられない。数えるのは startSkipped。</summary>
        Start = 0,
        /// <summary>同期ワードから次の同期ワードまでが 80 ビットでない。</summary>
        Length = 1,
        /// <summary>BCD の 1 の位が 9 を超える。</summary>
        Bcd = 2,
    }

    private long _accepted;
    private long _startSkipped;
    private long _rejectedLength;
    private long _rejectedBcd;
    private long _parityMismatch;
    private long _bit27Ones;
    private long _bit59Ones;
    private readonly long[] _lastLogTicks = [long.MinValue, long.MinValue, long.MinValue];
    private int _exitSummaryLogged;

    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long StartSkipped => Interlocked.Read(ref _startSkipped);
    internal long RejectedLength => Interlocked.Read(ref _rejectedLength);
    internal long RejectedBcd => Interlocked.Read(ref _rejectedBcd);
    /// <summary>受け取った LTC のフレームのうち、80 ビットの中の 0 の数が奇数だったもの（極性補正ビットが合っていない）。</summary>
    internal long ParityMismatch => Interlocked.Read(ref _parityMismatch);
    /// <summary>受け取った LTC のフレームのうち bit 27 が 1 だったもの（どちらが補正ビットかを実測で見るため）。</summary>
    internal long Bit27Ones => Interlocked.Read(ref _bit27Ones);
    /// <summary>受け取った LTC のフレームのうち bit 59 が 1 だったもの。</summary>
    internal long Bit59Ones => Interlocked.Read(ref _bit59Ones);

    internal void RecordAccepted(bool parityMismatch, bool bit27, bool bit59)
    {
        Interlocked.Increment(ref _accepted);
        if (parityMismatch) Interlocked.Increment(ref _parityMismatch);
        if (bit27) Interlocked.Increment(ref _bit27Ones);
        if (bit59) Interlocked.Increment(ref _bit59Ones);
    }

    /// <summary>落とした LTC のフレームを数え、Debug に理由つきで 1 行（同じ理由は 1 秒に 1 行まで）。</summary>
    internal void RecordRejected(RejectReason reason, string detail)
    {
        switch (reason)
        {
            case RejectReason.Start: Interlocked.Increment(ref _startSkipped); break;
            case RejectReason.Length: Interlocked.Increment(ref _rejectedLength); break;
            case RejectReason.Bcd: Interlocked.Increment(ref _rejectedBcd); break;
        }

        long now = Stopwatch.GetTimestamp();
        ref long last = ref _lastLogTicks[(int)reason];
        if (last != long.MinValue && now - last < Stopwatch.Frequency)
            return;
        last = now;
        Log.Debug("LTC frame check: dropped reason={Reason} {Detail}", reason, detail);
    }

    internal string FormatSummaryFields() =>
        $"rejectedBcd={RejectedBcd} rejectedLength={RejectedLength} startSkipped={StartSkipped} accepted={Accepted} " +
        $"parityMismatch={ParityMismatch} bit27Ones={Bit27Ones} bit59Ones={Bit59Ones}";

    /// <summary>要約を Information で 1 行（`Sync hold summary` と同じ時点・同じ体裁。配布ビルドでも数えられる）。</summary>
    internal void LogSummary(string source) =>
        Log.Information("LTC frame check summary: {Fields:l} source={Source}", FormatSummaryFields(), source);

    /// <summary>アプリの終了で 1 回だけ出す（終了の手順の入口と Dispose の両方から呼ばれ得る）。</summary>
    internal void LogSummaryAtExit()
    {
        if (Interlocked.Exchange(ref _exitSummaryLogged, 1) != 0)
            return;
        LogSummary("app-exit");
    }
}
