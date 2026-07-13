using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimecodeFrameDiagnosticsTests
{
    [Fact]
    public void Analyze_ReturnsNormal_WhenTimecodeAdvancesWithinExpectedRange()
    {
        var diagnostics = new TimecodeFrameDiagnostics();

        diagnostics.Analyze(10.0, 30.0);
        TimecodeFrameDiagnosticResult result = diagnostics.Analyze(10.033, 30.0);

        result.Status.Should().Be(TimecodeFrameDiagnosticStatus.Normal);
    }

    [Fact]
    public void Analyze_ReturnsDuplicate_WhenTimecodeDoesNotAdvance()
    {
        var diagnostics = new TimecodeFrameDiagnostics();

        diagnostics.Analyze(10.0, 30.0);
        TimecodeFrameDiagnosticResult result = diagnostics.Analyze(10.0, 30.0);

        result.Status.Should().Be(TimecodeFrameDiagnosticStatus.Duplicate);
    }

    [Fact]
    public void Analyze_ReturnsJump_WhenTimecodeMovesBySeveralFrames()
    {
        var diagnostics = new TimecodeFrameDiagnostics();

        diagnostics.Analyze(10.0, 30.0);
        TimecodeFrameDiagnosticResult result = diagnostics.Analyze(10.5, 30.0);

        result.Status.Should().Be(TimecodeFrameDiagnosticStatus.Jump);
    }

    [Fact]
    public void Analyze_ReturnsJump_WhenTimecodeMovesBackwardBySeveralFrames()
    {
        var diagnostics = new TimecodeFrameDiagnostics();

        diagnostics.Analyze(10.0, 30.0);
        TimecodeFrameDiagnosticResult result = diagnostics.Analyze(9.9, 30.0);

        result.Status.Should().Be(TimecodeFrameDiagnosticStatus.Jump);
    }

    [Fact]
    public void Analyze_ReturnsReverse_WhenTimecodeBrieflyMovesBackward()
    {
        var diagnostics = new TimecodeFrameDiagnostics();

        diagnostics.Analyze(10.0, 30.0);
        TimecodeFrameDiagnosticResult result = diagnostics.Analyze(9.96, 30.0);

        result.Status.Should().Be(TimecodeFrameDiagnosticStatus.Reverse);
    }
}
