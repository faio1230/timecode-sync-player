using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.5: OutputEngine.SetCanvas の GPU 抜き状態機械 CanvasSwapPlan。</summary>
public class CanvasSwapPlanTests
{
    [Fact]
    public void WithoutRequest_NothingIsRetained()
    {
        var plan = new CanvasSwapPlan();

        plan.NewImagePublished();
        plan.CanDiscardOld(activeOldLeases: 0).Should().BeFalse();
        plan.HasRetiredGeneration.Should().BeFalse();
    }

    [Fact]
    public void RetainsOldUntilNewImagePublished()
    {
        var plan = new CanvasSwapPlan();
        plan.Request();

        plan.HasRetiredGeneration.Should().BeTrue();
        plan.CanDiscardOld(activeOldLeases: 0).Should().BeFalse("新画像の公開前に旧画像を消すと黒を挟む");
    }

    [Fact]
    public void RetainsOldUntilAllLeasesReturn()
    {
        var plan = new CanvasSwapPlan();
        plan.Request();
        plan.NewImagePublished();

        plan.CanDiscardOld(activeOldLeases: 2).Should().BeFalse();
        plan.CanDiscardOld(activeOldLeases: 1).Should().BeFalse();
        plan.CanDiscardOld(activeOldLeases: 0).Should().BeTrue();
    }

    [Fact]
    public void Discarded_ResetsState()
    {
        var plan = new CanvasSwapPlan();
        plan.Request();
        plan.NewImagePublished();
        plan.CanDiscardOld(0).Should().BeTrue();

        plan.Discarded();

        plan.HasRetiredGeneration.Should().BeFalse();
        plan.NewGenerationPublished.Should().BeFalse();
        plan.CanDiscardOld(0).Should().BeFalse();
    }

    [Fact]
    public void RetiredPool_WithRealLeases_IsDiscardedOnlyAfterLastReturn()
    {
        var pool = new LatestPool(3);
        int slot = pool.TryBeginWrite();
        pool.Publish(slot, new ImageStamp(1, 17), gpuComplete: true);
        var plan = new CanvasSwapPlan();
        plan.Request();
        plan.NewImagePublished();

        var lease = pool.AcquireLatest()!;
        plan.CanDiscardOld(pool.ActiveReaders).Should().BeFalse();
        lease.Dispose();
        plan.CanDiscardOld(pool.ActiveReaders).Should().BeTrue();
    }
}
