namespace Ago.Chat.Concurrency.Tests;

internal static class ConcurrencyTestHelpers
{
    /// <summary>testing.md: poll a condition with a timeout instead of Thread.Sleep - both tests
    /// here wait on a background consumer, so there is no single awaitable for "done."</summary>
    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return await condition();
    }
}
