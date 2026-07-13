using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimecodeSyncFrameGateTests
{
    [Theory]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Initial))]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Normal))]
    public void ShouldApplySync_ReturnsTrue_ForUsableFrames(string statusName)
    {
        var status = Enum.Parse<TimecodeFrameDiagnosticStatus>(statusName);

        TimecodeSyncFrameGate.ShouldApplySync(status).Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Duplicate))]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Jump))]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Reverse))]
    [InlineData(nameof(TimecodeFrameDiagnosticStatus.Invalid))]
    public void ShouldApplySync_ReturnsFalse_ForUnintendedFrames(string statusName)
    {
        var status = Enum.Parse<TimecodeFrameDiagnosticStatus>(statusName);

        TimecodeSyncFrameGate.ShouldApplySync(status).Should().BeFalse();
    }
}
