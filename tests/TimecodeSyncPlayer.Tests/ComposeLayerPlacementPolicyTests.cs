using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.2: クリップ切替で Held／Freeze を再配置しない。</summary>
public class ComposeLayerPlacementPolicyTests
{
    private static readonly ClipPlacement Current = new(FitWidth.FitId);
    private static readonly ClipPlacement Held = new(FitHeight.FitId);
    private static readonly ClipPlacement Frozen = new(null);

    [Fact]
    public void DrawAcquired_UsesCurrentClipPlacement()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawAcquired, Current, Held, Frozen)
            .Should().Be(Current);
    }

    [Fact]
    public void DrawHeld_KeepsPlacementFromWhenImageWasAcquired()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawHeld, Current, Held, Frozen)
            .Should().Be(Held);
    }

    [Fact]
    public void DrawFrozen_KeepsPlacementFromWhenFreezeWasCaptured()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawFrozen, Current, Held, Frozen)
            .Should().Be(Frozen);
    }

    [Fact]
    public void DrawBlack_UsesCurrentPlacementWithoutDrawing()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawBlack, Current, Held, Frozen)
            .Should().Be(Current);
    }
}
