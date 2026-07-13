using System.Windows.Threading;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class StartupTimerFactoryTests
{
    [Fact]
    public void CreateStartedTimer_ConfiguresIntervalAndStartsTimer()
    {
        int ticks = 0;

        DispatcherTimer timer = StartupTimerFactory.CreateStartedTimer(
            TimeSpan.FromMilliseconds(200),
            (_, _) => ticks++);

        try
        {
            timer.Interval.Should().Be(TimeSpan.FromMilliseconds(200));
            timer.IsEnabled.Should().BeTrue();
        }
        finally
        {
            timer.Stop();
        }
    }
}
