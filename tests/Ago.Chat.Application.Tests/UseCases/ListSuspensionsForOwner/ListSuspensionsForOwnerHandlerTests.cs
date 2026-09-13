using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListSuspensionsForOwner;

public class ListSuspensionsForOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ReturnsOnlyCurrentlySuspendedSites()
    {
        var suspensions = new FakeSiteSuspensionReadStore();
        var suspendedSite = new SiteId(Guid.NewGuid());
        var expiredSite = new SiteId(Guid.NewGuid());
        suspensions.Suspend(suspendedSite, Now.AddMinutes(10));
        suspensions.Suspend(expiredSite, Now.AddMinutes(-10));
        var handler = new Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwnerHandler(
            suspensions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwner(), CancellationToken.None);

        var summary = Assert.Single(result);
        Assert.Equal(suspendedSite, summary.SiteId);
    }

    [Fact]
    public async Task HandleAsync_WhenNobodyIsSuspended_ReturnsAnEmptyList()
    {
        var handler = new Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwnerHandler(
            new FakeSiteSuspensionReadStore(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ListSuspensionsForOwner.ListSuspensionsForOwner(), CancellationToken.None);

        Assert.Empty(result);
    }
}
