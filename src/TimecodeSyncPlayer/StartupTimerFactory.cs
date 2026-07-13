using System.Windows.Threading;

namespace TimecodeSyncPlayer;

internal static class StartupTimerFactory
{
    public static DispatcherTimer CreateStartedTimer(TimeSpan interval, EventHandler tickHandler)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval
        };
        timer.Tick += tickHandler;
        timer.Start();
        return timer;
    }
}
