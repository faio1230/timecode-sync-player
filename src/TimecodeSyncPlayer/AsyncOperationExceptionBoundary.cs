namespace TimecodeSyncPlayer;

internal static class AsyncOperationExceptionBoundary
{
    public static async Task RunAsync(
        Func<Task> operation,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(reportFailure);

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            reportFailure(ex);
        }
    }
}
