using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2b: <see cref="LtcInputState"/> の書き換えメソッド。各メソッドが下ろす／立てるものを
/// 1 件ずつ確かめる（移す前の代入と同じ値・同じ残し方であること）。
/// </summary>
public class LtcInputStateTests
{
    [Fact]
    public void DiscardPendingJump_LowersSecondsAndFrameEndOnly()
    {
        var input = new LtcInputState();
        input.HoldPendingJump(3.0, receivedAt: 100, frameEndTimestamp: 200);

        input.DiscardPendingJump();

        input.PendingJumpSeconds.Should().BeNull();
        input.PendingJumpFrameEndTimestamp.Should().Be(0);
        input.PendingJumpReceivedAt.Should().Be(100, "移す前の DiscardPendingJump は受信時刻を残す");
    }

    [Fact]
    public void ClearPendingJumpSeconds_LowersOnlySeconds()
    {
        var input = new LtcInputState();
        input.HoldPendingJump(3.0, receivedAt: 100, frameEndTimestamp: 200);

        input.ClearPendingJumpSeconds();

        input.PendingJumpSeconds.Should().BeNull();
        input.PendingJumpFrameEndTimestamp.Should().Be(200, "確認の分岐はフレーム終端を読むために残す");
        input.PendingJumpReceivedAt.Should().Be(100, "確認の分岐は受信時刻を読むために残す");
    }

    [Fact]
    public void HoldPendingJump_SetsAllThree()
    {
        var input = new LtcInputState();

        input.HoldPendingJump(3.0, receivedAt: 100, frameEndTimestamp: 200);

        input.PendingJumpSeconds.Should().Be(3.0);
        input.PendingJumpReceivedAt.Should().Be(100);
        input.PendingJumpFrameEndTimestamp.Should().Be(200);
    }

    [Fact]
    public void DiscardPendingSync_LowersTheGroup()
    {
        var input = new LtcInputState();
        input.HoldPendingSync(4.0, 4.5, 300);

        input.DiscardPendingSync();

        input.Pending.Should().BeNull();
    }

    [Fact]
    public void HoldPendingSync_SetsAllThree()
    {
        var input = new LtcInputState();

        input.HoldPendingSync(4.0, 4.5, 300);

        input.Pending.Should().Be(new LtcInputState.PendingSync(4.0, 4.5, 300));
    }

    [Fact]
    public void AcceptFrame_SetsTheFrameAndLastApplied()
    {
        var input = new LtcInputState();

        input.AcceptFrame(5.0, 4.8, 400);

        input.Accepted.Should().Be(new LtcInputState.AcceptedFrame(5.0, 4.8, 400));
        input.LastAppliedLtcSeconds.Should().Be(5.0);
    }

    [Fact]
    public void MarkLastApplied_SetsValueOnly()
    {
        var input = new LtcInputState();

        input.MarkLastApplied(6.0);

        input.LastAppliedLtcSeconds.Should().Be(6.0);
        input.Accepted.Should().BeNull("最後に適用した値だけを書く");
    }

    [Fact]
    public void MarkHeldEffective_SetsValue()
    {
        var input = new LtcInputState();

        input.MarkHeldEffective(7.0);

        input.LastHeldEffectiveSeconds.Should().Be(7.0);
    }

    [Fact]
    public void ClearHeldEffective_LowersValue()
    {
        var input = new LtcInputState();
        input.MarkHeldEffective(7.0);

        input.ClearHeldEffective();

        input.LastHeldEffectiveSeconds.Should().BeNull();
    }

    [Fact]
    public void MarkHeldLossLanding_SetsValue()
    {
        var input = new LtcInputState();

        input.MarkHeldLossLanding(8.0);

        input.HeldLossLandingSeconds.Should().Be(8.0);
    }

    [Fact]
    public void ClearHeldLossLanding_LowersValue()
    {
        var input = new LtcInputState();
        input.MarkHeldLossLanding(8.0);

        input.ClearHeldLossLanding();

        input.HeldLossLandingSeconds.Should().BeNull();
    }

    [Fact]
    public void MarkJumpApplied_SetsLatch()
    {
        var input = new LtcInputState();

        input.MarkJumpApplied();

        input.JumpAppliedOnce.Should().BeTrue();
    }

    [Fact]
    public void ClearJumpApplied_LowersLatch()
    {
        var input = new LtcInputState();
        input.MarkJumpApplied();

        input.ClearJumpApplied();

        input.JumpAppliedOnce.Should().BeFalse();
    }

    [Fact]
    public void MarkHeldReapplied_SetsLatch()
    {
        var input = new LtcInputState();

        input.MarkHeldReapplied();

        input.HeldReapplyDone.Should().BeTrue();
    }

    [Fact]
    public void ClearHeldReapplied_LowersLatch()
    {
        var input = new LtcInputState();
        input.MarkHeldReapplied();

        input.ClearHeldReapplied();

        input.HeldReapplyDone.Should().BeFalse();
    }

    [Fact]
    public void MarkFollowStart_SetsLatch()
    {
        var input = new LtcInputState();

        input.MarkFollowStart();

        input.FollowStartPending.Should().BeTrue();
    }

    [Fact]
    public void ClearFollowStart_LowersLatch()
    {
        var input = new LtcInputState();
        input.MarkFollowStart();

        input.ClearFollowStart();

        input.FollowStartPending.Should().BeFalse();
    }

    [Fact]
    public void OnNormalFrame_LowersTwoLatchesAndTwoHeldValues()
    {
        var input = SeedAll();

        input.OnNormalFrame();

        input.JumpAppliedOnce.Should().BeFalse();
        input.HeldReapplyDone.Should().BeFalse();
        input.LastHeldEffectiveSeconds.Should().BeNull();
        input.HeldLossLandingSeconds.Should().BeNull();
        input.FollowStartPending.Should().BeTrue("このメソッドは追従開始に触れない");
    }

    [Fact]
    public void ClearFrameHistory_LowersFrameHistoryAndOneShotLatches()
    {
        var input = SeedAll();

        input.ClearFrameHistory();

        input.Accepted.Should().BeNull();
        input.LastAppliedLtcSeconds.Should().BeNull();
        input.LastHeldEffectiveSeconds.Should().BeNull();
        input.HeldLossLandingSeconds.Should().BeNull();
        input.Pending.Should().BeNull();
        input.PendingJumpSeconds.Should().BeNull();
        input.PendingJumpFrameEndTimestamp.Should().Be(0);
        input.PendingJumpReceivedAt.Should().Be(100, "移す前の ClearFrameHistory は未確認 Jump の受信時刻を残す");
        input.JumpAppliedOnce.Should().BeFalse();
        input.HeldReapplyDone.Should().BeFalse();
        input.FollowStartPending.Should().BeTrue("移す前の ClearFrameHistory は追従開始に触れない");
    }

    private static LtcInputState SeedAll()
    {
        var input = new LtcInputState();
        input.HoldPendingJump(3.0, receivedAt: 100, frameEndTimestamp: 200);
        input.HoldPendingSync(4.0, 4.5, 300);
        input.AcceptFrame(5.0, 4.8, 400);
        input.MarkLastApplied(6.0);
        input.MarkHeldEffective(7.0);
        input.MarkHeldLossLanding(8.0);
        input.MarkJumpApplied();
        input.MarkHeldReapplied();
        input.MarkFollowStart();
        return input;
    }
}
