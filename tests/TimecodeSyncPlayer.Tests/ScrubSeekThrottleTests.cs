using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 F-3・R-3・F-2: スクラブの間引き（前のシークが着地するまで次を送らない）。</summary>
public sealed class ScrubSeekThrottleTests
{
    [Fact]
    public void Move_WhenNotInFlight_Sends()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();

        throttle.Move(1.0).Should().Be(1.0);
        throttle.InFlight.Should().BeTrue();
    }

    [Fact]
    public void Move_BeforeBegin_SendsNothing()
    {
        var throttle = new ScrubSeekThrottle();

        throttle.Move(1.0).Should().BeNull();
        throttle.InFlight.Should().BeFalse();
    }

    [Fact]
    public void Move_WhileInFlight_KeepsOnlyTheLatest_AndLandedSendsOne()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0).Should().Be(1.0);

        throttle.Move(2.0).Should().BeNull();
        throttle.Move(3.0).Should().BeNull();
        throttle.Move(4.0).Should().BeNull();
        throttle.Pending.Should().Be(4.0);

        throttle.Landed().Should().Be(4.0, "only the latest target is sent after landing");
        throttle.InFlight.Should().BeTrue("the sent target is the single seek in flight");
        throttle.Pending.Should().BeNull();
    }

    [Fact]
    public void Landed_WithNothingPending_SendsNothing_AndClearsFlight()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);

        throttle.Landed().Should().BeNull();
        throttle.InFlight.Should().BeFalse();
        throttle.Move(2.0).Should().Be(2.0, "after landing the next move is sent at once");
    }

    [Fact]
    public void Landed_WhenPendingEqualsLastSent_SendsNothing()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);
        throttle.Move(2.0);
        throttle.Move(1.0);   // 戻ってきた: 最後に送った値と同じ

        throttle.Landed().Should().BeNull("the same value as the last sent seek is not sent again");
        throttle.InFlight.Should().BeFalse();
    }

    [Fact]
    public void Release_WhileInFlight_StillSends()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);
        throttle.Move(2.0);

        throttle.Release(3.0).Should().Be(3.0, "the release always sends the last position");
        throttle.InFlight.Should().BeTrue();
        throttle.IsActive.Should().BeFalse();
        throttle.Pending.Should().BeNull();
    }

    [Fact]
    public void Release_SendsEvenTheSameValueAsTheLastSent()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);
        throttle.Landed();

        throttle.Release(1.0).Should().Be(1.0);
    }

    [Fact]
    public void LandedAfterRelease_DoesNotSendTheOldTarget()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);       // 飛行中
        throttle.Move(2.0);       // 覚える
        throttle.Release(3.0);    // 最後の 1 本

        throttle.Landed().Should().BeNull("the remembered target is dropped after release");
        throttle.InFlight.Should().BeFalse();
        throttle.Move(4.0).Should().BeNull("moves after the release are ignored until the next press");
    }

    [Fact]
    public void SequenceOfOneDrag_SendsOnlyOneAtATime()
    {
        var throttle = new ScrubSeekThrottle();
        var sent = new List<double>();
        throttle.Begin();
        void Send(double? value)
        {
            if (value is not double v) return;
            throttle.InFlight.Should().BeTrue();
            sent.Add(v);
        }

        Send(throttle.Move(1));
        Send(throttle.Move(2));
        Send(throttle.Move(3));
        Send(throttle.Landed());     // 3
        Send(throttle.Move(4));
        Send(throttle.Move(5));
        Send(throttle.Landed());     // 5
        Send(throttle.Landed());     // 何もしない（覚えた目標なし）
        Send(throttle.Release(6));
        Send(throttle.Landed());     // 離した後は送らない

        sent.Should().Equal(1, 3, 5, 6);
    }

    [Fact]
    public void NewPress_WhileTheReleaseSeekIsInFlight_WaitsForItsLanding()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);
        throttle.Release(2.0);

        throttle.Begin();
        throttle.Move(5.0).Should().BeNull("only one seek is ever in flight, including the previous release");
        throttle.Landed().Should().Be(5.0);
    }

    [Fact]
    public void SendFailed_ClearsTheFlight_SoTheNextMoveIsSent()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);

        throttle.SendFailed();

        throttle.InFlight.Should().BeFalse();
        throttle.Move(2.0).Should().Be(2.0);
    }

    [Fact]
    public void HasLanded_ComparesTheDeliveredGenerationWithTheSeekGeneration()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);

        throttle.HasLanded(10).Should().BeFalse("the seek generation is not known yet");
        throttle.SetSeekGeneration(7);
        throttle.SeekGeneration.Should().Be(7UL);
        throttle.HasLanded(6).Should().BeFalse("a frame of the generation before the seek is not a landing");
        throttle.HasLanded(7).Should().BeTrue("the delivered generation caught up with the seek");
        throttle.HasLanded(9).Should().BeTrue("a later generation (another seek or a load) also passed it");
    }

    [Fact]
    public void HasLanded_IsFalse_WhenNothingIsInFlight()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.SetSeekGeneration(3);

        throttle.HasLanded(5).Should().BeFalse();
    }

    [Fact]
    public void NewSend_ForgetsThePreviousSeekGeneration()
    {
        var throttle = new ScrubSeekThrottle();
        throttle.Begin();
        throttle.Move(1.0);
        throttle.SetSeekGeneration(4);
        throttle.Move(2.0);
        throttle.Landed().Should().Be(2.0);

        throttle.HasLanded(4).Should().BeFalse("the new seek's generation is not known until it is set");
        throttle.SetSeekGeneration(5);
        throttle.HasLanded(4).Should().BeFalse();
        throttle.HasLanded(5).Should().BeTrue();
    }

    [Theory]
    [InlineData(0UL, 0UL, false)]   // 配信が無い
    [InlineData(0UL, 1UL, false)]
    [InlineData(3UL, 4UL, false)]
    [InlineData(4UL, 4UL, true)]
    [InlineData(5UL, 4UL, true)]
    public void IsLanded_UsesTheSameComparisonAsPositionFeedback(ulong delivered, ulong seek, bool expected)
    {
        ScrubSeekThrottle.IsLanded(delivered, seek).Should().Be(expected);
    }

    [Fact]
    public void FlightStats_CountsSentAndFlights()
    {
        var stats = new ScrubFlightStats();
        stats.NoteSent();
        stats.NoteSent();
        stats.NoteSent();
        stats.NoteFlight(40);
        stats.NoteFlight(10);
        stats.NoteFlight(25);

        stats.Sent.Should().Be(3);
        stats.MedianMs().Should().Be(25);
        stats.MaxMs().Should().Be(40);
        stats.SentPerSecond(1.5).Should().Be(2.0);

        stats.Reset();
        stats.Sent.Should().Be(0);
        double.IsNaN(stats.MedianMs()).Should().BeTrue();
    }
}
