using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 R-4: 1 フレーム送り・戻しの目標の計算と、飛行中の積み上げ。</summary>
public sealed class FrameStepperTests
{
    public static readonly TheoryData<double> AllFps = new()
    {
        24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 50, 60000.0 / 1001, 60,
    };

    /// <summary>格子の線ちょうどの PTS（ns に切り捨て・切り上げの両方）から送る・戻すと、隣のフレームの中に置く。</summary>
    [Theory]
    [MemberData(nameof(AllFps))]
    public void Target_FromExactGridLine_IsInsideTheNeighbourFrame(double fps)
    {
        foreach (long n in new long[] { 1, 2, 7, 29, 30, 59, 60, 299, 1799, 17982, 107892 })
        {
            double exactNs = n * 1_000_000_000.0 / fps;
            foreach (double ns in new[] { Math.Floor(exactNs), Math.Ceiling(exactNs), exactNs })
            {
                double pts = ns / 1_000_000_000.0;
                long current = FrameStepMath.FrameIndexAt(pts, fps);
                current.Should().Be(n, $"fps={fps} n={n} pts={pts:R}");

                foreach (int steps in new[] { 1, -1, 10, -10 })
                {
                    if (n + steps < 0) continue; // 先頭の端は別の試験で見る。
                    double target = FrameStepMath.TargetSeconds(current + steps, fps);
                    // 目標はフレームの頭と次の頭のちょうど中（半フレーム先）。
                    double head = (n + steps) / fps;
                    (target - head).Should().BeApproximately(0.5 / fps, 1e-9);
                    // 目標の秒は丸めても隣のフレームに落ちない。
                    FrameStepMath.FrameIndexAt(target, fps).Should().Be(n + steps);
                    FrameStepMath.FrameIndexAt(Math.Round(target, 3), fps).Should().Be(n + steps,
                        "rounding the target to milliseconds keeps it inside the frame");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllFps))]
    public void LastFrameIndex_MatchesFrameCount(double fps)
    {
        // 300 フレームの素材の長さ（格子の線ちょうど）。
        double duration = 300 / fps;
        FrameStepMath.LastFrameIndex(duration, fps).Should().Be(299);
        FrameStepMath.LastFrameIndex(Math.Round(duration, 6), fps).Should().Be(299);
        FrameStepMath.LastFrameIndex(0, fps).Should().Be(long.MaxValue);
    }

    [Fact]
    public void Press_WhilePaused_SendsOneFrameFromTheDeliveredFrame()
    {
        var fx = new Fx { Fps = 30000.0 / 1001, Delivered = new(100 * 1001 / 30000.0, 5) };
        var stepper = fx.Create();

        FrameStepOutcome outcome = stepper.Press(1);

        outcome.Kind.Should().Be(FrameStepOutcomeKind.Sent);
        outcome.BaseFrame.Should().Be(100);
        outcome.TargetFrame.Should().Be(101);
        fx.Seeks.Should().ContainSingle().Which.Should().BeApproximately(101.5 * 1001 / 30000.0, 1e-9);
        fx.PauseCalls.Should().Be(0);
        stepper.InFlight.Should().BeTrue();
    }

    [Fact]
    public void Press_WithoutDeliveredFrame_DoesNothing()
    {
        var fx = new Fx { Fps = 25, Delivered = null };
        var stepper = fx.Create();

        stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.NoDelivery);
        stepper.Press(-1).Kind.Should().Be(FrameStepOutcomeKind.NoDelivery);

        fx.Seeks.Should().BeEmpty();
        fx.PauseCalls.Should().Be(0, "nothing happens, not even a pause");
        stepper.InFlight.Should().BeFalse();
    }

    [Fact]
    public void Press_WithoutFps_DoesNothing()
    {
        var fx = new Fx { Fps = 0, Delivered = new(1.0, 3) };
        fx.Create().Press(1).Kind.Should().Be(FrameStepOutcomeKind.NoFps);
        fx.Seeks.Should().BeEmpty();
    }

    [Fact]
    public void Press_WhilePlaying_PausesFirstThroughTheUserPausePath_ThenStepsFromTheFrameAfterPause()
    {
        var control = new PlaybackControlState();
        control.TogglePlayPause().IsPaused.Should().BeFalse("playing");
        var fx = new Fx { Fps = 25, Delivered = new(4.0, 7) };
        var order = new List<string>();
        fx.OnPause = () =>
        {
            order.Add("pause");
            // MainWindow は再生/一時停止ボタンと同じ経路（TogglePlayPause）を渡す。
            control.TogglePlayPause();
            fx.Delivered = new(4.04, 7);
        };
        fx.IsPausedFunc = () => control.IsPaused;
        fx.OnSeek = _ => order.Add("seek");
        var stepper = fx.Create();

        FrameStepOutcome outcome = stepper.Press(1);

        order.Should().Equal("pause", "seek");
        control.IsPaused.Should().BeTrue();
        control.UserPauseOwned.Should().BeTrue("the pause is the user's, like the play/pause button");
        outcome.BaseFrame.Should().Be(101, "the frame shown after pausing is the base");
        outcome.TargetFrame.Should().Be(102);
    }

    [Fact]
    public void PressesInFlight_AccumulateIntoTheTarget_AndOneSeekIsSentAfterLanding()
    {
        var fx = new Fx { Fps = 60, Delivered = new(1.0, 2), CurrentGeneration = 3 };
        var stepper = fx.Create();

        stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.Sent);
        for (int i = 0; i < 9; i++)
            stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.Queued);
        fx.Seeks.Should().HaveCount(1, "only one seek is in flight");
        stepper.PendingSteps.Should().Be(9);

        // まだ着地していない（配信の世代が古い）。
        stepper.ObserveLanding(3, 2, 1.0).Kind.Should().Be(FrameStepOutcomeKind.None);
        fx.Seeks.Should().HaveCount(1);

        fx.CurrentGeneration = 4;
        FrameStepOutcome next = stepper.ObserveLanding(3, 3, 61 / 60.0);
        next.Kind.Should().Be(FrameStepOutcomeKind.Sent);
        next.BaseFrame.Should().Be(61);
        next.TargetFrame.Should().Be(70, "ten presses move ten frames");
        fx.Seeks.Should().HaveCount(2);

        FrameStepOutcome done = stepper.ObserveLanding(4, 4, 70 / 60.0);
        done.Kind.Should().Be(FrameStepOutcomeKind.Landed);
        stepper.InFlight.Should().BeFalse();
        fx.Seeks.Should().HaveCount(2);
    }

    [Fact]
    public void ForwardAndBackPressesInFlight_CancelOut()
    {
        var fx = new Fx { Fps = 25, Delivered = new(2.0, 2), CurrentGeneration = 3 };
        var stepper = fx.Create();
        stepper.Press(1);
        stepper.Press(1);
        stepper.Press(-1);
        stepper.Press(1);
        stepper.Press(-1);
        stepper.ObserveLanding(3, 3, 2.04).Kind.Should().Be(FrameStepOutcomeKind.Landed);
        fx.Seeks.Should().HaveCount(1);
    }

    [Fact]
    public void GenerationUnknownAtSend_IsTakenFromTheFirstSample()
    {
        var fx = new Fx { Fps = 25, Delivered = new(2.0, 2), CurrentGeneration = null };
        var stepper = fx.Create();
        stepper.Press(1);
        stepper.GenerationKnown.Should().BeFalse();
        stepper.ObserveLanding(5, 4, 2.0).Kind.Should().Be(FrameStepOutcomeKind.None);
        stepper.SeekGeneration.Should().Be(5UL);
        stepper.ObserveLanding(5, 5, 2.06).Kind.Should().Be(FrameStepOutcomeKind.Landed);
    }

    [Fact]
    public void BackAtTheFirstFrame_DoesNotGoBelowZero()
    {
        var fx = new Fx { Fps = 30000.0 / 1001, Delivered = new(0.0, 1), Duration = 10.01 };
        var stepper = fx.Create();
        stepper.Press(-1).Kind.Should().Be(FrameStepOutcomeKind.AtEdge);
        fx.Seeks.Should().BeEmpty();

        // 2 フレーム目から 5 回戻すと 0 で止まる。
        fx.Delivered = new(2 * 1001 / 30000.0, 1);
        fx.CurrentGeneration = 2;
        FrameStepOutcome first = stepper.Press(-1);
        first.TargetFrame.Should().Be(1);
        for (int i = 0; i < 4; i++) stepper.Press(-1);
        FrameStepOutcome next = stepper.ObserveLanding(2, 2, 1 * 1001 / 30000.0);
        next.TargetFrame.Should().Be(0);
        fx.Seeks.Should().OnlyContain(s => s > 0);
    }

    [Fact]
    public void ForwardAtTheLastFrame_DoesNotGoPastTheDuration()
    {
        double fps = 60000.0 / 1001;
        double duration = 600 / fps; // 600 フレーム（最後は 599）
        var fx = new Fx { Fps = fps, Delivered = new(599 / fps, 1), Duration = duration };
        var stepper = fx.Create();
        stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.AtEdge);
        fx.Seeks.Should().BeEmpty();

        fx.Delivered = new(597 / fps, 1);
        fx.CurrentGeneration = 2;
        stepper.Press(1).TargetFrame.Should().Be(598);
        for (int i = 0; i < 5; i++) stepper.Press(1);
        FrameStepOutcome next = stepper.ObserveLanding(2, 2, 598 / fps);
        next.TargetFrame.Should().Be(599);
        fx.Seeks.Should().OnlyContain(s => s < duration);
    }

    [Fact]
    public void LandingAtTheEdgeWithPendingSteps_SendsNothing()
    {
        double fps = 25;
        var fx = new Fx { Fps = fps, Delivered = new(98 / fps, 1), Duration = 4.0, CurrentGeneration = 2 };
        var stepper = fx.Create();
        stepper.Press(1).TargetFrame.Should().Be(99);
        stepper.Press(1);
        stepper.ObserveLanding(2, 2, 99 / fps).Kind.Should().Be(FrameStepOutcomeKind.Landed);
        fx.Seeks.Should().HaveCount(1);
        stepper.InFlight.Should().BeFalse();
    }

    [Fact]
    public void Ended_ReleasesTheFlight()
    {
        var fx = new Fx { Fps = 25, Delivered = new(1.0, 1), CurrentGeneration = 2 };
        var stepper = fx.Create();
        stepper.Press(1);
        stepper.Ended().Kind.Should().Be(FrameStepOutcomeKind.Landed);
        stepper.InFlight.Should().BeFalse();
        stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.Sent);
    }

    [Fact]
    public void FailedSeek_DoesNotStayInFlight()
    {
        var fx = new Fx { Fps = 25, Delivered = new(1.0, 1), SeekResult = false };
        var stepper = fx.Create();
        stepper.Press(1).Kind.Should().Be(FrameStepOutcomeKind.SendFailed);
        stepper.InFlight.Should().BeFalse();
    }

    private sealed class Fx
    {
        public double Fps;
        public double Duration;
        public FrameStepDelivered? Delivered;
        public ulong? CurrentGeneration = 1;
        public bool SeekResult = true;
        public bool Paused = true;
        public Func<bool>? IsPausedFunc;
        public Action? OnPause;
        public Action<double>? OnSeek;
        public int PauseCalls;
        public List<double> Seeks { get; } = [];

        public FrameStepper Create() => new(new FrameStepEffects(
            IsPaused: () => IsPausedFunc?.Invoke() ?? Paused,
            PauseAsUser: () => { PauseCalls++; OnPause?.Invoke(); },
            ReadDelivered: () => Delivered,
            Fps: () => Fps,
            Duration: () => Duration,
            Seek: s => { Seeks.Add(s); OnSeek?.Invoke(s); return SeekResult; },
            ReadCurrentGeneration: () => CurrentGeneration));
    }
}
