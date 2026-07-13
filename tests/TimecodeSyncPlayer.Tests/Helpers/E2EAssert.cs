namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class E2EAssert
{
    public static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                    return;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException(
            lastException == null
                ? "Condition was not satisfied before timeout."
                : $"Condition was not satisfied before timeout. Last error: {lastException.Message}",
            lastException);
    }
}
