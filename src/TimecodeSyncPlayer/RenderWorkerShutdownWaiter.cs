namespace TimecodeSyncPlayer;

internal static class RenderWorkerShutdownWaiter
{
    public static void Wait(Task? task, Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(reportFailure);
        if (task == null)
            return;

        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            reportFailure(ex);
        }
    }
}
