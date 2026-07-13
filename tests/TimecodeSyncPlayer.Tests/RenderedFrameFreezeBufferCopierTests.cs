using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderedFrameFreezeBufferCopierTests
{
    [Fact]
    public void CopyIfNeeded_CopiesRenderedFrame_WhenPolicyAllowsCopy()
    {
        using var bufferManager = new PixelBufferManager();
        bufferManager.EnsurePixelBuffer(2, 2);
        bufferManager.PixelBuffer!.AsSpan(0, 16).Fill(42);
        var copier = new RenderedFrameFreezeBufferCopier(bufferManager);

        bool copied = copier.CopyIfNeeded(GapState.WaitingForFrameStep, 2, 2);

        copied.Should().BeTrue();
        bufferManager.FrozenFrameBuffer.Should().NotBeNull();
        bufferManager.FrozenFrameBuffer!.Take(16).Should().OnlyContain(value => value == 42);
    }

    [Fact]
    public void CopyIfNeeded_DoesNotCopy_WhenPolicyDoesNotAllowCopy()
    {
        using var bufferManager = new PixelBufferManager();
        bufferManager.EnsurePixelBuffer(2, 2);
        var copier = new RenderedFrameFreezeBufferCopier(bufferManager);

        bool copied = copier.CopyIfNeeded(GapState.Inactive, 2, 2);

        copied.Should().BeFalse();
        bufferManager.FrozenFrameBuffer.Should().NotBeNull();
        bufferManager.FrozenFrameBuffer!.Take(16).Should().OnlyContain(value => value == 0);
    }
}
