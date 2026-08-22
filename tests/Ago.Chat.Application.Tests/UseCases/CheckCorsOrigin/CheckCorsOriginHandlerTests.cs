using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CheckCorsOrigin;

public class CheckCorsOriginHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenAnySiteAllowsTheOrigin_ReturnsTrue()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "demo_site", ["https://shop.example.com"]);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var handler = new CheckCorsOriginHandler(sites, new FakeCache());

        var result = await handler.HandleAsync(new CheckOriginAllowed("https://shop.example.com"), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSiteAllowsTheOrigin_ReturnsFalse()
    {
        var handler = new CheckCorsOriginHandler(new FakeSiteRepository(), new FakeCache());

        var result = await handler.HandleAsync(new CheckOriginAllowed("https://evil.example.com"), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForTheSameKnownOrigin_OnlyReadsTheRepositoryOnce()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "demo_site", ["https://shop.example.com"]);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var handler = new CheckCorsOriginHandler(sites, new FakeCache());

        await handler.HandleAsync(new CheckOriginAllowed("https://shop.example.com"), CancellationToken.None);
        await handler.HandleAsync(new CheckOriginAllowed("https://shop.example.com"), CancellationToken.None);

        Assert.Equal(1, sites.OriginLookupCalls);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForAnUnknownOrigin_OnlyReadsTheRepositoryOnce()
    {
        var sites = new FakeSiteRepository();
        var handler = new CheckCorsOriginHandler(sites, new FakeCache());

        await handler.HandleAsync(new CheckOriginAllowed("https://evil.example.com"), CancellationToken.None);
        await handler.HandleAsync(new CheckOriginAllowed("https://evil.example.com"), CancellationToken.None);

        Assert.Equal(1, sites.OriginLookupCalls); // negative caching (caching.md) - not just the positive path
    }
}
