using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderContextCreateResultTests
{
    [Fact]
    public void FromReturnCode_ReturnsSuccess_WhenRcIsZero()
    {
        RenderContextCreateResult result = RenderContextCreateResult.FromReturnCode(IntPtr.Zero, rc: 0);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void FromReturnCode_ReturnsFailure_WhenRcIsNegative()
    {
        RenderContextCreateResult result = RenderContextCreateResult.FromReturnCode(IntPtr.Zero, rc: -1);

        result.Success.Should().BeFalse();
    }
}
