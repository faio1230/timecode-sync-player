using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.2/4.5: 現在クリップの Fit を ClipPlacement に写す。</summary>
public class TimelineOutputStateTests
{
    private static PlaylistTrack TrackWithFit(string? fit) => new(
        Id: Guid.NewGuid(),
        FilePath: "clip.mp4",
        Name: "clip",
        MediaIn: TimeSpan.Zero,
        MediaOut: null,
        TimelineOffset: TimeSpan.Zero,
        MediaDuration: TimeSpan.FromSeconds(10),
        SyncOffset: TimeSpan.Zero,
        FrameRate: 30,
        IsEnabled: true,
        Fit: fit);

    [Fact]
    public void PlacementFor_NoTrack_InheritsProjectDefault()
    {
        TimelineOutputState.PlacementFor(null).FitId.Should().BeNull();
    }

    [Fact]
    public void PlacementFor_TrackWithoutFit_InheritsProjectDefault()
    {
        TimelineOutputState.PlacementFor(TrackWithFit(null)).FitId.Should().BeNull();
    }

    [Theory]
    [InlineData(FitHeight.FitId)]
    [InlineData(FitWidth.FitId)]
    public void PlacementFor_TrackFit_IsCopiedToClipPlacement(string fit)
    {
        TimelineOutputState.PlacementFor(TrackWithFit(fit)).FitId.Should().Be(fit);
    }

    // K3 f4-14: 最終フレームへのシークの直後、ポンプ中の照会がパイプライン値（尺 + 2 フレーム）へ落ち、
    // 連続性の補正でその値に固定される。合成層の Freeze の保存は照会位置ではなく Freeze の目標と比べる。
    [Fact]
    public void FreezeComparisonSeconds_UsesFreezeTarget_WhenQueriedPositionIsHeldAtPipelineValue()
    {
        TimelineOutputState state = TimelineOutputState.Default with
        {
            Gap = OutputGapMode.GapFreeze,
            PositionSeconds = 20.033333333,
            FreezeTargetSeconds = 19.983333333,
        };

        state.FreezeComparisonSeconds.Should().Be(19.983333333);
    }

    [Fact]
    public void FreezeComparisonSeconds_WithoutFreezeTarget_UsesQueriedPosition()
    {
        TimelineOutputState state = TimelineOutputState.Default with { PositionSeconds = 12.5 };

        state.FreezeComparisonSeconds.Should().Be(12.5);
    }
}
