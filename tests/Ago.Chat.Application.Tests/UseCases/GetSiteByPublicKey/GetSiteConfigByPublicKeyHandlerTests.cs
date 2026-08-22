using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetSiteByPublicKey;

public class GetSiteConfigByPublicKeyHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenTheSiteExists_ReturnsItsConfig()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "demo_site", ["https://example.com"]);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var handler = new GetSiteConfigByPublicKeyHandler(sites, new FakeCache());

        var result = await handler.HandleAsync(new GetSiteConfigByPublicKey("demo_site"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(site.Id.Value, result.SiteId);
        Assert.Equal("demo_site", result.PublicKey);
        Assert.Equal(["https://example.com"], result.AllowedOrigins);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsNull()
    {
        var handler = new GetSiteConfigByPublicKeyHandler(new FakeSiteRepository(), new FakeCache());

        var result = await handler.HandleAsync(new GetSiteConfigByPublicKey("no_such_site"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForTheSamePublicKey_OnlyReadsTheRepositoryOnce()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "demo_site", []);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var handler = new GetSiteConfigByPublicKeyHandler(sites, new FakeCache());

        await handler.HandleAsync(new GetSiteConfigByPublicKey("demo_site"), CancellationToken.None);
        await handler.HandleAsync(new GetSiteConfigByPublicKey("demo_site"), CancellationToken.None);

        Assert.Equal(1, sites.LookupCalls);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForAMissingSite_OnlyReadsTheRepositoryOnce()
    {
        var sites = new FakeSiteRepository();
        var handler = new GetSiteConfigByPublicKeyHandler(sites, new FakeCache());

        await handler.HandleAsync(new GetSiteConfigByPublicKey("no_such_site"), CancellationToken.None);
        await handler.HandleAsync(new GetSiteConfigByPublicKey("no_such_site"), CancellationToken.None);

        Assert.Equal(1, sites.LookupCalls); // negative caching (caching.md) - not just the positive path
    }
}
