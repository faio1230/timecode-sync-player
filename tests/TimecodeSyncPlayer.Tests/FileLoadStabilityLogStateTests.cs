using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class FileLoadStabilityLogStateTests
{
    [Fact]
    public void ShouldLog_ReturnsTrue_OnFirstAttempt()
    {
        var state = new FileLoadStabilityLogState(TimeSpan.FromSeconds(1));

        bool result = state.ShouldLog(DateTime.UtcNow);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldLog_ReturnsFalse_WithinInterval()
    {
        var state = new FileLoadStabilityLogState(TimeSpan.FromSeconds(1));
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(now);

        bool result = state.ShouldLog(now.AddMilliseconds(500));

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldLog_ReturnsTrue_AfterInterval()
    {
        var state = new FileLoadStabilityLogState(TimeSpan.FromSeconds(1));
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(now);

        bool result = state.ShouldLog(now.AddSeconds(1));

        result.Should().BeTrue();
    }

    [Fact]
    public void Reset_AllowsImmediateLogAgain()
    {
        var state = new FileLoadStabilityLogState(TimeSpan.FromSeconds(1));
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(now);
        state.ShouldLog(now.AddMilliseconds(500));

        state.Reset();
        bool result = state.ShouldLog(now.AddMilliseconds(600));

        result.Should().BeTrue();
    }
}
