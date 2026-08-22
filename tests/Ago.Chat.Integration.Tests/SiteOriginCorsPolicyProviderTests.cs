using Ago.Chat.Api.Cors;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-01`, layer 1: `SiteOriginCorsPolicyProvider` is exercised directly, not through a full HTTP
/// pipeline/`TestServer` - the ASP.NET Core CORS *middleware* (turning a non-null `CorsPolicy` into
/// real `Access-Control-Allow-Origin` response headers) is framework code this project does not
/// re-test, matching how every other endpoint here is proven by calling its handler method directly
/// (`RateLimitingTests`, `SendMessageOutboxTests`) rather than standing up a real server. What this
/// project owns, and what needs a real test, is the allow/deny decision itself - "does any seeded
/// site's `AllowedOrigins` contain this `Origin`" - against real Postgres and real Redis.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class SiteOriginCorsPolicyProviderTests(SiteCachingFixture fixture)
{
    [Fact]
    public async Task GetPolicyAsync_WhenSomeSiteAllowsTheOrigin_ReturnsAPolicyEchoingIt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example.com"]));
            await db.SaveChangesAsync();
        }

        var provider = new SiteOriginCorsPolicyProvider();
        var context = BuildHttpContext("https://shop.example.com");

        var policy = await provider.GetPolicyAsync(context, policyName: null);

        Assert.NotNull(policy);
        Assert.Contains("https://shop.example.com", policy.Origins);
    }

    [Fact]
    public async Task GetPolicyAsync_WhenNoSiteAllowsTheOrigin_ReturnsNull()
    {
        var provider = new SiteOriginCorsPolicyProvider();
        var context = BuildHttpContext("https://evil.example.com");

        var policy = await provider.GetPolicyAsync(context, policyName: null);

        Assert.Null(policy);
    }

    [Fact]
    public async Task GetPolicyAsync_WhenNoOriginHeaderIsPresent_ReturnsNull_WithoutEvenChecking()
    {
        var provider = new SiteOriginCorsPolicyProvider();
        var context = BuildHttpContext(origin: null);

        var policy = await provider.GetPolicyAsync(context, policyName: null);

        Assert.Null(policy); // same-origin caller - no CORS headers needed, not "origin unknown"
    }

    private HttpContext BuildHttpContext(string? origin)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISiteRepository>(new SiteRepository(fixture.CreateDbContext()));
        services.AddSingleton<ICache>(new RedisCache(
            fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance));
        services.AddScoped<CheckCorsOriginHandler>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        return context;
    }
}
