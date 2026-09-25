using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2e: <see cref="BoundaryHoldState"/> の各メソッドが変える値・段階の遷移を 1 件ずつ。
/// </summary>
public class BoundaryHoldStateTests
{
    [Fact]
    public void MarkHeld_SetsFlag()
    {
        var state = new BoundaryHoldState();

        state.MarkHeld();

        state.IsHeld.Should().BeTrue();
    }

    [Fact]
    public void ClearHeld_LowersFlag()
    {
        var state = new BoundaryHoldState();
        state.MarkHeld();

        state.ClearHeld();

        state.IsHeld.Should().BeFalse();
    }

    [Fact]
    public void NoteSeek_SetsTargetAndEpoch()
    {
        var state = new BoundaryHoldState();

        state.NoteSeek(20.0, epoch: 5);

        state.Seek.Should().Be(new BoundaryHoldState.BoundarySeek(20.0, 5));
    }

    [Fact]
    public void NoteSeek_Null_ClearsTheRecord()
    {
        var state = new BoundaryHoldState();
        state.NoteSeek(20.0, epoch: 5);

        state.NoteSeek(null, epoch: 6);

        state.Seek.Should().BeNull("端でなければ記録しない（読み込み番号も読まれない）");
    }

    [Fact]
    public void ClearSeek_LowersRecord()
    {
        var state = new BoundaryHoldState();
        state.NoteSeek(20.0, epoch: 5);

        state.ClearSeek();

        state.Seek.Should().BeNull();
    }

    [Fact]
    public void InitialState_IsNotHeldAndHasNoSeek()
    {
        var state = new BoundaryHoldState();

        state.IsHeld.Should().BeFalse();
        state.Seek.Should().BeNull();
    }
}
