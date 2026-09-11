using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class MpvSnapshotSourceTests
{
    private static RenderedFrameSnapshot Frame(int width, int height, int generation, long sequence)
        => RenderedFrameSnapshot.Copy(new byte[width * height * 4], width, height, generation, sequence, 0);

    private static MpvSnapshotSource<int> CreateSource()
        => new(
            [0, 1, 2, 3],
            (slot, pixels, width, height) => null,
            slot => new SourceImageDescription(null, 4, 4, SourceImageFormat.Bgra8));

    private sealed class FakeCompletion : IUploadCompletion
    {
        public bool Complete;
        public bool Disposed;
        public bool TryComplete() => Complete;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TryUpload_KeepsTheImageInvisibleUntilTheGpuCopyCompletes()
    {
        var completions = new Queue<FakeCompletion>();
        var source = new MpvSnapshotSource<int>(
            [0, 1, 2, 3],
            (slot, pixels, width, height) => { var completion = new FakeCompletion(); completions.Enqueue(completion); return completion; },
            slot => new SourceImageDescription(null, 4, 4, SourceImageFormat.Bgra8));
        source.SetGeneration(1);
        source.TryUpload(Frame(4, 4, 1, 1), 1, 0).Should().BeTrue();
        source.PendingUploads.Should().Be(1);
        source.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);

        completions.Peek().Complete = true;
        source.PollUploads();
        source.PendingUploads.Should().Be(0);
        completions.Peek().Disposed.Should().BeTrue();
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(1);
        lease.Dispose();
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void TryUpload_OffersOnlyTheCurrentGenerationAndReleasesTheFrameLease()
    {
        var source = CreateSource();
        source.SetGeneration(1);

        var current = Frame(4, 4, 1, 1);
        source.TryUpload(current, 1, 0.0).Should().BeTrue();
        source.TryAcquire(1, 0.0, out var lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(1);
        lease.Stamp.Generation.Should().Be(1);
        lease.Stamp.PositionSeconds.Should().Be(0.0);
        lease.Texture.Should().BeNull();

        var stale = Frame(4, 4, 0, 2);
        source.TryUpload(stale, 0, 0.5).Should().BeFalse();
        FluentActions.Invoking(() => _ = current.Pixels).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => _ = stale.Pixels).Should().Throw<ObjectDisposedException>();
        source.DroppedUploads.Should().Be(1);
        lease.Dispose();
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void TryUpload_SelectsLatestAtOrBeforePositionElseTheNextImage()
    {
        var source = CreateSource();
        source.SetGeneration(1);
        source.TryUpload(Frame(4, 4, 1, 1), 1, 1.0);
        source.TryUpload(Frame(4, 4, 1, 2), 1, 2.0);
        source.TryUpload(Frame(4, 4, 1, 3), 1, 3.0);

        source.TryAcquire(1, 2.5, out var lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(2);
        lease.Dispose();
        source.TryAcquire(1, 0.5, out lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(1);
        lease.Dispose();
        source.TryAcquire(1, 9.0, out lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(3);
        lease.Dispose();
        source.TryAcquire(1, 2.5, out _).Should().Be(SourceStatus.Ready);
        source.TryAcquire(2, 2.5, out _).Should().Be(SourceStatus.NotReady);
    }

    [Fact]
    public void TryUpload_IsDroppedWhenEveryImageIsLeased()
    {
        var source = CreateSource();
        source.SetGeneration(1);
        source.TryUpload(Frame(4, 4, 1, 1), 1, 0);
        source.TryUpload(Frame(4, 4, 1, 2), 1, 1);
        source.TryUpload(Frame(4, 4, 1, 3), 1, 2);
        source.TryAcquire(1, 2, out var a).Should().Be(SourceStatus.Ready);
        source.TryAcquire(1, 1, out var b).Should().Be(SourceStatus.Ready);
        source.TryAcquire(1, 0, out var c).Should().Be(SourceStatus.Ready);

        // アップロード自体は受け付けるが、全画像 lease 中のためリングへの公開は破棄される。
        var blocked = Frame(4, 4, 1, 4);
        source.TryUpload(blocked, 1, 3).Should().BeTrue();
        source.PendingUploads.Should().Be(0);
        source.Uploaded.Should().Be(3);
        source.DroppedUploads.Should().Be(1);
        FluentActions.Invoking(() => _ = blocked.Pixels).Should().Throw<ObjectDisposedException>();

        a!.Dispose();
        source.TryUpload(Frame(4, 4, 1, 5), 1, 4).Should().BeTrue();
        source.TryAcquire(1, 4, out var latest).Should().Be(SourceStatus.Ready);
        latest!.Stamp.Sequence.Should().Be(5);
        latest.Dispose();
        b!.Dispose();
        c!.Dispose();
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void SetGeneration_RetiresImagesAndFreeSlotsBecomeReusable()
    {
        var source = CreateSource();
        source.SetGeneration(1);
        source.TryUpload(Frame(4, 4, 1, 1), 1, 0);
        source.SetGeneration(2);
        source.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);
        source.TryAcquire(2, 0, out _).Should().Be(SourceStatus.NotReady);
        // 旧世代は退避済みで slot は free に戻り、新世代のアップロードを受け付ける。
        source.TryUpload(Frame(4, 4, 2, 2), 2, 0).Should().BeTrue();
        source.TryAcquire(2, 0, out var lease).Should().Be(SourceStatus.Ready);
        lease!.Stamp.Sequence.Should().Be(2);
        lease.Dispose();
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void Lease_ReturningIsRequiredBeforeDispose()
    {
        var source = CreateSource();
        source.SetGeneration(1);
        source.TryUpload(Frame(4, 4, 1, 1), 1, 0);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ready);
        source.TryDispose().Should().BeFalse();
        FluentActions.Invoking(() => source.Dispose()).Should().Throw<InvalidOperationException>();
        lease!.Dispose();
        source.TryDispose().Should().BeTrue();
        source.Dispose();
    }

    [Fact]
    public void SnapshotInputMailbox_KeepsOnlyLatestAndDisposesReplacedFrames()
    {
        using var mailbox = new SnapshotInputMailbox();
        var first = Frame(4, 4, 1, 1);
        var second = Frame(4, 4, 1, 2);
        mailbox.Publish(first, 1, 0.5);
        mailbox.Publish(second, 1, 0.6);

        FluentActions.Invoking(() => _ = first.Pixels).Should().Throw<ObjectDisposedException>();
        mailbox.TryTake(out var taken, out int generation, out double position).Should().BeTrue();
        taken.Should().BeSameAs(second);
        generation.Should().Be(1);
        position.Should().Be(0.6);
        taken!.Dispose();
        mailbox.TryTake(out _, out _, out _).Should().BeFalse();

        var pending = Frame(4, 4, 1, 3);
        mailbox.Publish(pending, 1, 0.7);
        mailbox.Dispose();
        FluentActions.Invoking(() => _ = pending.Pixels).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TimelineOutputMailbox_KeepsOnlyLatestState()
    {
        var mailbox = new TimelineOutputMailbox();
        mailbox.Take().Should().BeNull();
        var first = TimelineOutputState.Default with { Gap = OutputGapMode.Black };
        var second = TimelineOutputState.Default with { Gap = OutputGapMode.GapFreeze, PositionSeconds = 3.5 };
        mailbox.Publish(first);
        mailbox.Publish(second);
        mailbox.Take().Should().Be(second);
        mailbox.Take().Should().BeNull();
    }

    [Theory]
    [InlineData((int)OutputGapMode.None, true, false, false, (int)LayerAction.DrawAcquired)]
    [InlineData((int)OutputGapMode.None, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.None, false, false, false, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.Hold, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.Hold, false, false, false, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.Black, true, true, true, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.GapFreeze, false, false, true, (int)LayerAction.DrawFrozen)]
    [InlineData((int)OutputGapMode.GapFreeze, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.GapFreeze, false, false, false, (int)LayerAction.DrawBlack)]
    public void ComposeLayerPolicy_HoldsWithoutInsertingBlackForNotReady(
        int gap, bool acquired, bool held, bool frozen, int expected)
    {
        ComposeLayerPolicy.Decide((OutputGapMode)gap, acquired, held, frozen).Should().Be((LayerAction)expected);
    }
}
