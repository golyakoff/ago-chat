namespace Ago.Chat.Integration.Tests;

internal static class OutboxTestHelpers
{
    /// <summary>testing.md: "poll a condition with a timeout" instead of Thread.Sleep - dispatch
    /// happens on a background loop, so there is no single awaitable for "done".</summary>
    public static Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout);

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
