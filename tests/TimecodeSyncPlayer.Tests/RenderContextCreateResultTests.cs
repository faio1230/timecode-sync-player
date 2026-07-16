using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderContextCreateResultTests
{
    [Fact]
    public void FromReturnCode_ReturnsSuccess_WhenRcIsZero()
    {
        var context = new IntPtr(123);
        RenderContextCreateResult result = RenderContextCreateResult.FromReturnCode(context, rc: 0);

        result.Success.Should().BeTrue();
        result.RenderContext.Should().Be(context);
        result.ReturnCode.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void FromReturnCode_ReturnsFailureAndPreservesDiagnostics_WhenRcIsNegative(int rc)
    {
        var context = new IntPtr(456);
        RenderContextCreateResult result = RenderContextCreateResult.FromReturnCode(context, rc);

        result.Success.Should().BeFalse();
        result.RenderContext.Should().Be(context);
        result.ReturnCode.Should().Be(rc);
    }
}
