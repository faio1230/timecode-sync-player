using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class SpoutStageRingTests
{
    private static ImageStamp Stamp(long id) => new(id, id * 10);

    [Fact]
    public void StageCompleteSendSuccessFreesSlotAndUpdatesHeld()
    {
        var ring = new SpoutStageRing(2);
        int slot = ring.BeginStage(Stamp(1), out _);
        slot.Should().Be(0);
        ring.CompleteStage(slot, Stamp(1));
        ring.ReadyCount.Should().Be(1);

        int send = ring.TryBeginSend(out var stamp);
        send.Should().Be(0);
        stamp.Id.Should().Be(1);
        ring.EndSend(send, stamp, sent: true, sendFence: 0);

        ring.Held.Id.Should().Be(1);
        ring.FreeCount.Should().Be(2);
    }

    [Fact]
    public void CompleteStageSupersedesUnsentReady()
    {
        var ring = new SpoutStageRing(2);
        int first = ring.BeginStage(Stamp(1), out _);
        ring.CompleteStage(first, Stamp(1));
        int second = ring.BeginStage(Stamp(2), out _);
        second.Should().Be(1);
        ring.CompleteStage(second, Stamp(2));

        ring.ReadyCount.Should().Be(1);
        ring.TryBeginSend(out var stamp).Should().Be(1);
        stamp.Id.Should().Be(2);
    }

    [Fact]
    public void BeginStageReusesReadyWhenTheOtherSlotIsSending()
    {
        var ring = new SpoutStageRing(2);
        int sending = ring.BeginStage(Stamp(1), out _);
        ring.CompleteStage(sending, Stamp(1));
        ring.TryBeginSend(out _).Should().Be(sending);
        int ready = ring.BeginStage(Stamp(2), out _);
        ring.CompleteStage(ready, Stamp(2));
        ring.ReadyCount.Should().Be(1);

        ring.BeginStage(Stamp(3), out _).Should().Be(ready);
        ring.CompleteStage(ready, Stamp(3));
        ring.TryBeginSend(out var stamp).Should().Be(ready);
        stamp.Id.Should().Be(3);
    }

    [Fact]
    public void FailedSendRestoresReadyForRetry()
    {
        var ring = new SpoutStageRing(2);
        int slot = ring.BeginStage(Stamp(1), out _);
        ring.CompleteStage(slot, Stamp(1));
        int send = ring.TryBeginSend(out var stamp);

        ring.EndSend(send, stamp, sent: false, sendFence: 0);

        ring.Held.Id.Should().Be(0);
        ring.TryBeginSend(out var retry).Should().Be(slot);
        retry.Id.Should().Be(1);
    }

    [Fact]
    public void FailedSendFreesSlotWhenNewerReadyExists()
    {
        var ring = new SpoutStageRing(2);
        int older = ring.BeginStage(Stamp(1), out _);
        ring.CompleteStage(older, Stamp(1));
        int send = ring.TryBeginSend(out var stamp);

        int newer = ring.BeginStage(Stamp(2), out _);
        ring.CompleteStage(newer, Stamp(2));

        ring.EndSend(send, stamp, sent: false, sendFence: 0);
        ring.ReadyCount.Should().Be(1);
        ring.TryBeginSend(out var retry).Should().Be(newer);
        retry.Id.Should().Be(2);
    }

    [Fact]
    public void AbortStageFreesSlot()
    {
        var ring = new SpoutStageRing(2);
        int slot = ring.BeginStage(Stamp(1), out _);
        ring.AbortStage(slot);
        ring.FreeCount.Should().Be(2);
    }

    [Fact]
    public void SendFenceIsReturnedWhenTheSlotIsStagedAgain()
    {
        var ring = new SpoutStageRing(2);
        int first = ring.BeginStage(Stamp(1), out var pendingFirst);
        pendingFirst.Should().Be(0);
        ring.CompleteStage(first, Stamp(1));
        int send = ring.TryBeginSend(out var stamp);
        ring.EndSend(send, stamp, sent: true, sendFence: 42);

        int reused = ring.BeginStage(Stamp(2), out var pendingSecond);
        reused.Should().Be(send);
        pendingSecond.Should().Be(42);
        int other = ring.BeginStage(Stamp(3), out var pendingThird);
        other.Should().Be(1 - send);
        pendingThird.Should().Be(0);
    }
}
