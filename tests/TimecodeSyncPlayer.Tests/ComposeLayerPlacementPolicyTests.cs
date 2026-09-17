using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 段階 4.2: クリップ切替で Freeze を再配置しない。
/// D26: Held は合成済みキャンバスの複製をそのまま重ねるため配置を選ばない（テスト対象外）。
/// </summary>
public class ComposeLayerPlacementPolicyTests
{
    private static readonly ClipPlacement Current = new(FitWidth.FitId);
    private static readonly ClipPlacement Frozen = new(null);

    [Fact]
    public void DrawAcquired_UsesCurrentClipPlacement()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawAcquired, Current, Frozen)
            .Should().Be(Current);
    }

    [Fact]
    public void DrawHeld_UsesCurrentPlacement_ButCanvasCopyIsDrawnInstead()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawHeld, Current, Frozen)
            .Should().Be(Current);
    }

    [Fact]
    public void DrawFrozen_KeepsPlacementFromWhenFreezeWasCaptured()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawFrozen, Current, Frozen)
            .Should().Be(Frozen);
    }

    [Fact]
    public void DrawBlack_UsesCurrentPlacementWithoutDrawing()
    {
        ComposeLayerPolicy.SelectPlacement(LayerAction.DrawBlack, Current, Frozen)
            .Should().Be(Current);
    }
}
