using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 K3: ギャップの Freeze で、最終フレームへのシークが EOS と重なると
/// 直前の絵が残る型（A1、解析 docs/analysis/2026-09-17-A1-gap-freeze-frame-not-updated.md）の
/// 決定的な再現。シークの着地と EOS の前後を仮想時間で固定する。
///
/// 取り込みの経路は MainWindow の 3 か所を写す（取り込みの門を動かすのが目的なので、
/// 合成・GPU は含めない）:
/// - 進入: GapEnterCoordinator.StartGapFreezeCaptureCore（GapEnterCoordinator.cs:77-108）
/// - フレーム到着: MainWindow.OnSourceFrameReady（MainWindow.xaml.cs:2255-2285）
/// - 確定と時間切れ: MainWindow.TryCompleteGapFreeze / Tick（同 :2210-2248、:2097-2113）
///
/// EOS がシークの後を追い越す順序（検証機の F-4 の型）だけが、最終フレームを 1 枚も
/// 配信できない。D21 の確定門（GapFrameCaptureCoordinator.cs:32-33）は「進入後に届いた
/// フレーム」を要求するため、時間切れまで確定できず、D32 の遅延確定もフレームが来ないため発火しない。
/// C-1 はこの順序で EOS を観測したとき、既存の再シーク（GapFreezeHandler.TryBeginSeekRetryForEnded）で
/// 最終フレームを取り直す（MainWindow.xaml.cs の HandleGStreamerEnded → RetryGapFreezeSeekForEnded）。
/// </summary>
public class GapFreezeEosRaceTests
{
    private const double DurationSeconds = 20.0;
    private const double Fps = 30.0;
    private const double TargetSeconds = DurationSeconds - 1.0 / Fps;   // 19.9667（尺の直前）

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 0);

    private static (GapFreezeCapturePath Path, ScenarioPlayback Playback) Arrange()
    {
        var clock = NewClock();
        var playback = new ScenarioPlayback(
            positionSeconds: DurationSeconds - 2.0, durationSeconds: DurationSeconds, fps: Fps);
        playback.Load("C:/media/ltc_b.mp4", DurationSeconds - 2.0, paused: false);
        return (new GapFreezeCapturePath(clock, playback), playback);
    }

    /// <summary>
    /// 反対の順序（EOS → シークの着地）は shim が seek で EOS 状態を解除する
    /// （tcs_gstreamer.cpp:2819-2823）ため、届いたフレームで確定できる。この順序は今日も緑。
    /// </summary>
    [Fact]
    public void EosLandsBeforeTheSeek_TheSeekClearsTheEosState_AndTheFrameConfirms()
    {
        (GapFreezeCapturePath path, ScenarioPlayback playback) = Arrange();

        playback.MarkEnded();
        path.EnterGapFreeze(TargetSeconds);
        playback.Ended.Should().BeFalse("seek は shim の EOS 状態を解除する");

        path.SourceFrameReady(TargetSeconds);
        path.Tick(allowRedraw: true).Should().BeTrue("着地したフレームで確定する");
        path.Handler.CurrentState.Should().Be(GapState.FreezeComplete);
        path.Handler.CachedTargetKnown.Should().BeTrue();
    }

    /// <summary>進入後に目標フレームが届く通常順（D21-b）。今日も緑（対照）。</summary>
    [Fact]
    public void FrameArrivesAfterTheSeek_ConfirmsTheFreeze()
    {
        (GapFreezeCapturePath path, _) = Arrange();

        path.EnterGapFreeze(TargetSeconds);
        path.SourceFrameReady(TargetSeconds);
        path.Tick(allowRedraw: true).Should().BeTrue();

        path.Handler.CachedTargetKnown.Should().BeTrue();
        path.Handler.CachedTargetSeconds.Should().BeApproximately(TargetSeconds, 1e-9);
    }

    /// <summary>
    /// EOS がシークの後を追い越す順序（検証機の F-4 の型）。
    /// 「位置は目標（±2 フレーム）なのに、進入後に届いたフレームが 1 枚も無い」ため確定できないが、
    /// C-1（EOS を捕獲に配線し、既存の再シークで取り直す）で最終フレームまで到達することを固定する。
    /// C-1 を外すとこのテストは赤（時間切れで CachedTargetKnown == false）になる。
    /// </summary>
    [Fact]
    public void EosLandsAfterTheSeek_RecoveryReissuesTheSeek_AndConfirmsTheFinalFrame()
    {
        (GapFreezeCapturePath path, ScenarioPlayback playback) = Arrange();

        path.EnterGapFreeze(TargetSeconds);
        path.Handler.FrameSeenSinceCapture.Should().BeFalse();
        playback.MarkEnded();   // シークの着地と同時に EOS を観測（新しいフレームは来ない）

        path.Tick(allowRedraw: true);
        playback.TryGetTimePos(out double eosPosition).Should().BeTrue();
        eosPosition.Should().BeApproximately(DurationSeconds + 1.0 / Fps, 1e-6,
            "EOS 中の位置は pipeline の値（尺 + 1 フレーム = 20.033）へ落ちる");

        // C-1: EOS を観測したら、既存の再シーク（D21-b）で最終フレームを取り直す。
        path.SourceEnded().Should().BeTrue("捕捉中・フレーム未到着なので再シークを始める");
        path.Handler.SeekRetryCount.Should().Be(1);
        playback.Ended.Should().BeFalse("再シークは shim の EOS 状態を解除する");

        path.SourceFrameReady(TargetSeconds);    // 再シークが最終フレームを配信した
        path.Tick(allowRedraw: true).Should().BeTrue();

        path.Handler.CachedTargetKnown.Should().BeTrue(
            "EOS と重なった最終フレームのシークでも、フリーズは最終フレームで確定できること（A1 型）");
        path.Handler.CurrentState.Should().Be(GapState.FreezeComplete);
    }

    /// <summary>
    /// C-1 でまだ緑にならない型（残りの限界）: EOS が続いて再シークの上限（MaxSeekRetries = 2）を
    /// 使い切ると、3 秒の時間切れで Held のままになる。実機でこれが残る場合だけ C-2
    /// （目標位置での再ロード）を足す、という判断の境界を固定する。
    /// </summary>
    [Fact]
    public void RecoveryIsBounded_AfterMaxRetriesTheTimeoutKeepsTheHeldPicture()
    {
        (GapFreezeCapturePath path, ScenarioPlayback playback) = Arrange();

        path.EnterGapFreeze(TargetSeconds);
        for (int i = 0; i < GapFreezeHandler.MaxSeekRetries; i++)
        {
            playback.MarkEnded();
            path.SourceEnded().Should().BeTrue($"再シーク {i + 1} 回目は上限内");
        }

        playback.MarkEnded();
        path.SourceEnded().Should().BeFalse("上限（MaxSeekRetries）に達したら再シークしない");
        path.Handler.SeekRetryCount.Should().Be(GapFreezeHandler.MaxSeekRetries);

        for (int i = 0; i < 32; i++)
            path.Tick(allowRedraw: true);

        path.Handler.CachedTargetKnown.Should().BeFalse("フレームが来なければ時間切れで Held");
        path.Handler.CurrentState.Should().Be(GapState.FreezeComplete);
    }

    /// <summary>
    /// D32 の遅延確定（GapFreezeHandler.cs:221-241、MainWindow.xaml.cs:2263-2268）は、
    /// フレームが「遅れてでも届けば」打ち切り後に確定できる。今日も緑。
    /// 赤の再現との差は、EOS と重なった場合にフレームが 1 枚も届かないことだけ。
    /// </summary>
    [Fact]
    public void LateFrameAfterTheTimeout_ReopensAndConfirms()
    {
        (GapFreezeCapturePath path, _) = Arrange();

        path.EnterGapFreeze(TargetSeconds);
        for (int i = 0; i < 32; i++)
            path.Tick(allowRedraw: true);
        path.Handler.CachedTargetKnown.Should().BeFalse("タイムアウト時は最終画像なし");
        path.Handler.HasLateConfirmTarget.Should().BeTrue("D32 は目標を遅延確定のために残す");

        path.SourceFrameReady(TargetSeconds);   // 遅れて届いた目標フレーム（EOS の場合、これが来ない）
        path.Tick(allowRedraw: true).Should().BeTrue();
        path.Handler.CachedTargetKnown.Should().BeTrue();
    }

    /// <summary>
    /// <see cref="GapFreezeHandler"/> + 取り込みの門（GapFrameCaptureCoordinator / GapFreezeCaptureOperation）を
    /// MainWindow と同じ順で動かす。EOS・フレームの到着は源（shim/GStreamerSource）の契約として与える。
    /// </summary>
    private sealed class GapFreezeCapturePath
    {
        private readonly ScenarioClock _clock;
        private readonly ScenarioPlayback _playback;
        private readonly Guid _trackId = Guid.NewGuid();
        private bool _hasFrame;

        public GapFreezeCapturePath(ScenarioClock clock, ScenarioPlayback playback)
        {
            _clock = clock;
            _playback = playback;
            Handler = new GapFreezeHandler(clock);
        }

        public GapFreezeHandler Handler { get; }

        /// <summary>
        /// ギャップ進入（GapEnterCoordinator.cs:79-103）: PauseForGap → EnterFreezeCapture → SeekTo。
        /// 進入の待ちをシークより先に始める D21-b の順序も同じ。
        /// </summary>
        public void EnterGapFreeze(double targetSeconds)
        {
            _playback.SetPaused(true);
            Handler.EnterFreezeCapture(_trackId, targetSeconds, "C:/media/ltc_b.mp4");
            _playback.Seek(targetSeconds).Success.Should().BeTrue();
        }

        /// <summary>
        /// MainWindow.RetryGapFreezeSeekForEnded（C-1）: EOS を観測したときに捕捉の再シークを 1 回始める。
        /// 「捕獲中・目標フレーム未到着・上限内」の判断は製品側の
        /// <see cref="GapFreezeHandler.TryBeginSeekRetryForEnded"/> が持つ。
        /// </summary>
        public bool SourceEnded()
        {
            if (!Handler.TryBeginSeekRetryForEnded())
                return false;
            _playback.Seek(Handler.PendingTargetSeconds).Success.Should().BeTrue();
            return true;
        }

        /// <summary>
        /// MainWindow.OnSourceFrameReady（:2255-2285）: ソースフレームの位置が目標 ±2 フレームなら
        /// 「進入後に届いた」と数える。タイムアウト後は D32 の遅延確定を開き直す。
        /// </summary>
        public void SourceFrameReady(double positionSeconds)
        {
            if (Handler.CurrentState == GapState.FreezeComplete &&
                Handler.IsLateConfirmFrame(positionSeconds, Fps))
            {
                Handler.ReopenCaptureForLateFrame();
                _hasFrame = true;
                return;
            }

            if (Handler.CurrentState is not (GapState.EnteringFreeze or GapState.WaitingForFrameStep) ||
                Handler.FrameSeenSinceCapture ||
                !double.IsFinite(positionSeconds))
            {
                return;
            }

            if (Math.Abs(positionSeconds - Handler.PendingTargetSeconds) <= 2.0 / Fps)
            {
                Handler.NotifyFrameArrived();
                _hasFrame = true;
            }
        }

        /// <summary>
        /// MainWindow の 100ms タイマー 1 回（Tick :2097-2113）と、フレームコールバックでの確定
        /// （TryCompleteGapFreeze :2210-2248）。仮想時間を 100ms 進めてから判定する。
        /// </summary>
        public bool Tick(bool allowRedraw)
        {
            _clock.AdvanceMilliseconds(100);
            _playback.AdvanceTime(TimeSpan.FromMilliseconds(100));

            bool captured = false;
            if (Handler.CurrentState == GapState.EnteringFreeze && !_playback.IsSeeking() && _playback.Paused)
            {
                bool hasPosition = _playback.TryGetTimePos(out double position);
                GapFrameCaptureDecision decision = GapFrameCaptureCoordinator.Decide(
                    Handler.CurrentState, _hasFrame, true, hasPosition, position,
                    Handler.PendingTargetSeconds, Fps,
                    allowRedraw: allowRedraw,
                    frameSeenSinceCapture: Handler.FrameSeenSinceCapture);
                if (decision == GapFrameCaptureDecision.RenderAndCapture)
                {
                    captured = GapFreezeCaptureOperation.Run(Handler, _trackId,
                        () => true,
                        stillCurrent => stillCurrent() && IsNativeGapFreezeTargetReady());
                }
            }

            if (Handler.HasTimedOut())
                Handler.ForceFreezeComplete();
            return captured;
        }

        /// <summary>MainWindow.IsNativeGapFreezeTargetReady（:2381-2394）。</summary>
        private bool IsNativeGapFreezeTargetReady()
        {
            if (_playback.IsSeeking() || !_playback.Paused)
                return false;
            bool hasPosition = _playback.TryGetTimePos(out double position);
            return GapFrameCaptureCoordinator.Decide(
                Handler.CurrentState, true, true, hasPosition, position,
                Handler.PendingTargetSeconds, Fps,
                frameSeenSinceCapture: Handler.FrameSeenSinceCapture) ==
                GapFrameCaptureDecision.RenderAndCapture;
        }
    }
}
