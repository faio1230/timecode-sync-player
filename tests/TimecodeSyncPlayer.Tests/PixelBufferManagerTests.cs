using System.Runtime.InteropServices;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PixelBufferManagerTests
{
    // --- EnsurePixelBuffer / EnsureFrozenFrameBuffer / EnsureGapFreezeFrameBuffer ---

    [Fact]
    public void EnsurePixelBuffer_FirstCall_AllocatesExpectedSizeAndNonZeroPtr()
    {
        using var manager = new PixelBufferManager();

        manager.EnsurePixelBuffer(4, 3);

        manager.PixelBuffer.Should().NotBeNull();
        manager.PixelBuffer!.Length.Should().Be(4 * 3 * 4);
        manager.PixelPtr.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void EnsurePixelBuffer_SameSizeReCall_DoesNotReallocate()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(4, 3);
        byte[]? first = manager.PixelBuffer;

        manager.EnsurePixelBuffer(4, 3);

        manager.PixelBuffer.Should().BeSameAs(first);
    }

    [Fact]
    public void EnsurePixelBuffer_SmallerSizeReCall_DoesNotReallocate()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(10, 10);
        byte[]? first = manager.PixelBuffer;

        manager.EnsurePixelBuffer(2, 2);

        manager.PixelBuffer.Should().BeSameAs(first);
    }

    [Fact]
    public void EnsurePixelBuffer_LargerSizeReCall_Reallocates()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        byte[]? first = manager.PixelBuffer;

        manager.EnsurePixelBuffer(10, 10);

        manager.PixelBuffer.Should().NotBeSameAs(first);
        manager.PixelBuffer!.Length.Should().Be(10 * 10 * 4);
    }

    [Fact]
    public void EnsureFrozenFrameBuffer_FirstCall_AllocatesExpectedSizeAndNonZeroPtr()
    {
        using var manager = new PixelBufferManager();

        manager.EnsureFrozenFrameBuffer(4, 3);

        manager.FrozenFrameBuffer.Should().NotBeNull();
        manager.FrozenFrameBuffer!.Length.Should().Be(4 * 3 * 4);
        manager.FrozenFramePtr.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void EnsureFrozenFrameBuffer_SameOrSmallerSizeReCall_DoesNotReallocate()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(4, 3);
        byte[]? first = manager.FrozenFrameBuffer;

        manager.EnsureFrozenFrameBuffer(4, 3);
        manager.FrozenFrameBuffer.Should().BeSameAs(first);

        manager.EnsureFrozenFrameBuffer(1, 1);
        manager.FrozenFrameBuffer.Should().BeSameAs(first);
    }

    [Fact]
    public void EnsureFrozenFrameBuffer_LargerSizeReCall_Reallocates()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(2, 2);
        byte[]? first = manager.FrozenFrameBuffer;

        manager.EnsureFrozenFrameBuffer(10, 10);

        manager.FrozenFrameBuffer.Should().NotBeSameAs(first);
        manager.FrozenFrameBuffer!.Length.Should().Be(10 * 10 * 4);
    }

    [Fact]
    public void EnsureGapFreezeFrameBuffer_FirstCall_AllocatesExpectedSizeAndNonZeroPtr()
    {
        using var manager = new PixelBufferManager();

        manager.EnsureGapFreezeFrameBuffer(4, 3);

        manager.CachedGapFreezeFrameBuffer.Should().NotBeNull();
        manager.CachedGapFreezeFrameBuffer!.Length.Should().Be(4 * 3 * 4);
        manager.CachedGapFreezeFramePtr.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void EnsureGapFreezeFrameBuffer_SameOrSmallerSizeReCall_DoesNotReallocate()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureGapFreezeFrameBuffer(4, 3);
        byte[]? first = manager.CachedGapFreezeFrameBuffer;

        manager.EnsureGapFreezeFrameBuffer(4, 3);
        manager.CachedGapFreezeFrameBuffer.Should().BeSameAs(first);

        manager.EnsureGapFreezeFrameBuffer(1, 1);
        manager.CachedGapFreezeFrameBuffer.Should().BeSameAs(first);
    }

    [Fact]
    public void EnsureGapFreezeFrameBuffer_LargerSizeReCall_Reallocates()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureGapFreezeFrameBuffer(2, 2);
        byte[]? first = manager.CachedGapFreezeFrameBuffer;

        manager.EnsureGapFreezeFrameBuffer(10, 10);

        manager.CachedGapFreezeFrameBuffer.Should().NotBeSameAs(first);
        manager.CachedGapFreezeFrameBuffer!.Length.Should().Be(10 * 10 * 4);
    }

    // --- PixelPtr / FrozenFramePtr / CachedGapFreezeFramePtr default state ---

    [Fact]
    public void Ptrs_BeforeAnyEnsureCall_AreZero()
    {
        using var manager = new PixelBufferManager();

        manager.PixelPtr.Should().Be(IntPtr.Zero);
        manager.FrozenFramePtr.Should().Be(IntPtr.Zero);
        manager.CachedGapFreezeFramePtr.Should().Be(IntPtr.Zero);
    }

    // --- CopyToFrozenFrame ---

    [Fact]
    public void CopyToFrozenFrame_PixelBufferNotAllocated_DoesNothing()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(2, 2);

        Action act = () => manager.CopyToFrozenFrame(2, 2);

        act.Should().NotThrow();
        manager.FrozenFrameBuffer!.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void CopyToFrozenFrame_FrozenFrameTooSmall_DoesNothing()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(4, 4);
        manager.EnsureFrozenFrameBuffer(1, 1);
        byte[] frozenBefore = (byte[])manager.FrozenFrameBuffer!.Clone();

        Action act = () => manager.CopyToFrozenFrame(4, 4);

        act.Should().NotThrow();
        manager.FrozenFrameBuffer.Should().Equal(frozenBefore);
    }

    [Fact]
    public void CopyToFrozenFrame_ValidSizes_CopiesContent()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        manager.EnsureFrozenFrameBuffer(2, 2);
        FillWithPattern(manager.PixelBuffer!);

        manager.CopyToFrozenFrame(2, 2);

        manager.FrozenFrameBuffer.Should().Equal(manager.PixelBuffer);
    }

    // --- CopyFrozenToGapFreezeFrame ---

    [Fact]
    public void CopyFrozenToGapFreezeFrame_FrozenFrameNotAllocated_DoesNothing()
    {
        using var manager = new PixelBufferManager();

        Action act = () => manager.CopyFrozenToGapFreezeFrame(2, 2);

        act.Should().NotThrow();
        manager.CachedGapFreezeFrameBuffer.Should().BeNull();
        manager.CachedGapFreezeFrameWidth.Should().Be(0);
        manager.CachedGapFreezeFrameHeight.Should().Be(0);
    }

    [Fact]
    public void CopyFrozenToGapFreezeFrame_ValidSizes_AutoAllocatesCopiesAndRecordsDimensions()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(3, 2);
        FillWithPattern(manager.FrozenFrameBuffer!);

        manager.CopyFrozenToGapFreezeFrame(3, 2);

        manager.CachedGapFreezeFrameBuffer.Should().NotBeNull();
        manager.CachedGapFreezeFrameBuffer.Should().Equal(manager.FrozenFrameBuffer);
        manager.CachedGapFreezeFrameWidth.Should().Be(3);
        manager.CachedGapFreezeFrameHeight.Should().Be(2);
    }

    [Fact]
    public void CopyFrozenToGapFreezeFrame_FrozenFrameTooSmall_DoesNothing()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(1, 1);

        Action act = () => manager.CopyFrozenToGapFreezeFrame(4, 4);

        act.Should().NotThrow();
        manager.CachedGapFreezeFrameBuffer.Should().BeNull();
    }

    // --- ClearPixelBuffer ---

    [Fact]
    public void ClearPixelBuffer_ZeroesContentButKeepsSameBufferReference()
    {
        using var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        FillWithPattern(manager.PixelBuffer!);
        byte[]? bufferRef = manager.PixelBuffer;

        manager.ClearPixelBuffer();

        manager.PixelBuffer.Should().BeSameAs(bufferRef);
        manager.PixelBuffer!.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void ClearPixelBuffer_NotAllocated_DoesNotThrow()
    {
        using var manager = new PixelBufferManager();

        Action act = () => manager.ClearPixelBuffer();

        act.Should().NotThrow();
    }

    // --- ClearFrozenFrame / ClearGapFreezeFrame ---

    [Fact]
    public void ClearFrozenFrame_ResetsBufferToNullAndPtrToZero()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(2, 2);

        manager.ClearFrozenFrame();

        manager.FrozenFrameBuffer.Should().BeNull();
        manager.FrozenFramePtr.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void ClearFrozenFrame_NotAllocated_DoesNotThrow()
    {
        using var manager = new PixelBufferManager();

        Action act = () => manager.ClearFrozenFrame();

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearGapFreezeFrame_ResetsBufferPtrAndDimensions()
    {
        using var manager = new PixelBufferManager();
        manager.EnsureFrozenFrameBuffer(3, 2);
        manager.CopyFrozenToGapFreezeFrame(3, 2);

        manager.ClearGapFreezeFrame();

        manager.CachedGapFreezeFrameBuffer.Should().BeNull();
        manager.CachedGapFreezeFramePtr.Should().Be(IntPtr.Zero);
        manager.CachedGapFreezeFrameWidth.Should().Be(0);
        manager.CachedGapFreezeFrameHeight.Should().Be(0);
    }

    [Fact]
    public void ClearGapFreezeFrame_NotAllocated_DoesNotThrow()
    {
        using var manager = new PixelBufferManager();

        Action act = () => manager.ClearGapFreezeFrame();

        act.Should().NotThrow();
    }

    // --- InitFormatString / InitStridePtr ---

    [Fact]
    public void InitFormatString_AfterCall_PtrIsNonZero()
    {
        using var manager = new PixelBufferManager();

        manager.InitFormatString("0bgr0");

        manager.FormatStringPtr.Should().NotBe(IntPtr.Zero);
        Marshal.PtrToStringAnsi(manager.FormatStringPtr).Should().Be("0bgr0");
    }

    [Fact]
    public void InitFormatString_CalledTwice_DoesNotThrowAndUpdatesValue()
    {
        using var manager = new PixelBufferManager();
        manager.InitFormatString("0bgr0");

        Action act = () => manager.InitFormatString("rgba");

        act.Should().NotThrow();
        Marshal.PtrToStringAnsi(manager.FormatStringPtr).Should().Be("rgba");
    }

    [Fact]
    public void InitStridePtr_AfterCall_PtrIsNonZero()
    {
        using var manager = new PixelBufferManager();

        manager.InitStridePtr();

        manager.StridePtr.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void InitStridePtr_CalledTwice_DoesNotThrow()
    {
        using var manager = new PixelBufferManager();
        manager.InitStridePtr();

        Action act = () => manager.InitStridePtr();

        act.Should().NotThrow();
        manager.StridePtr.Should().NotBe(IntPtr.Zero);
    }

    // --- Dispose ---

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        manager.EnsureFrozenFrameBuffer(2, 2);
        manager.EnsureGapFreezeFrameBuffer(2, 2);
        manager.InitFormatString("0bgr0");
        manager.InitStridePtr();

        Action act = () => manager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_IsSafe()
    {
        var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        manager.Dispose();

        Action act = () => manager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithoutAnyAllocation_DoesNotThrow()
    {
        var manager = new PixelBufferManager();

        Action act = () => manager.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void AfterDispose_PtrPropertyAccessDoesNotCrash()
    {
        var manager = new PixelBufferManager();
        manager.EnsurePixelBuffer(2, 2);
        manager.EnsureFrozenFrameBuffer(2, 2);
        manager.EnsureGapFreezeFrameBuffer(2, 2);
        manager.Dispose();

        manager.PixelPtr.Should().Be(IntPtr.Zero);
        manager.FrozenFramePtr.Should().Be(IntPtr.Zero);
        manager.CachedGapFreezeFramePtr.Should().Be(IntPtr.Zero);
    }

    // --- SizeArray / SizeArrayPtr ---

    [Fact]
    public void SizeArrayPtr_IsNonZeroImmediatelyAfterConstruction()
    {
        using var manager = new PixelBufferManager();

        manager.SizeArrayPtr.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void SizeArray_WriteIsVisibleThroughSizeArrayPtr()
    {
        using var manager = new PixelBufferManager();

        manager.SizeArray[0] = 1920;
        manager.SizeArray[1] = 1080;

        Marshal.ReadInt32(manager.SizeArrayPtr, 0).Should().Be(1920);
        Marshal.ReadInt32(manager.SizeArrayPtr, sizeof(int)).Should().Be(1080);
    }

    private static void FillWithPattern(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i % 256);
        }
    }
}
