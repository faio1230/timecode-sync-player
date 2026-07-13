using System.Runtime.InteropServices;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class StartupBufferInitializerTests
{
    [Fact]
    public void Initialize_CreatesFormatStringAndStrideStorage()
    {
        using var bufferManager = new PixelBufferManager();
        var initializer = new StartupBufferInitializer(bufferManager);

        initializer.Initialize("bgr0");

        bufferManager.FormatStringPtr.Should().NotBe(IntPtr.Zero);
        Marshal.PtrToStringAnsi(bufferManager.FormatStringPtr).Should().Be("bgr0");
        bufferManager.StridePtr.Should().NotBe(IntPtr.Zero);
    }
}
