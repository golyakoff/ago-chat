using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-01`, layer 2 - the real per-site boundary, proven independently of CORS: two seeded sites, each
/// with its own distinct `AllowedOrigins` entry, so an origin that layer 1 would happily allow (it
/// belongs to *some* site) is still rejected when it does not belong to *this* request's own site.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class OriginAuthorizationTests(SiteCachingFixture fixture)
{
    [Fact]
    public async Task VisitorSessionEndpoint_WhenTheOriginBelongsToADifferentSite_IsRejected()
    {
        var (siteAPublicKey, _) = await SeedSiteAsync("https://site-a.example.com");
        var (_, _) = await SeedSiteAsync("https://site-b.example.com");

        // A caller claiming site A's public key, but arriving from site B's own approved origin -
        // layer 1 alone would let this through (site B's origin is known to *some* site).
        var status = await InvokeVisitorSessionAsync(siteAPublicKey, origin: "https://site-b.example.com");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task VisitorSessionEndpoint_WhenTheOriginBelongsToItsOwnSite_Succeeds()
    {
        var (publicKey, _) = await SeedSiteAsync("https://site-a.example.com");

        var status = await InvokeVisitorSessionAsync(publicKey, origin: "https://site-a.example.com");

        Assert.Equal(StatusCodes.Status201Created, status);
    }

    [Fact]
    public async Task VisitorSessionEndpoint_WhenNoOriginHeaderIsPresent_Succeeds()
    {
        var (publicKey, _) = await SeedSiteAsync("https://site-a.example.com");

        var status = await InvokeVisitorSessionAsync(publicKey, origin: null);

        Assert.Equal(StatusCodes.Status201Created, status); // a same-origin caller has no claim to verify
    }

    [Fact]
    public async Task HubConnection_WhenTheOriginBelongsToADifferentSite_IsRejected()
    {
        var (_, siteAId) = await SeedSiteAsync("https://site-a.example.com");
        await SeedSiteAsync("https://site-b.example.com");

        var validator = CreateValidator();
        var context = BuildHubCallerContext("https://site-b.example.com");

        var allowed = await validator.IsAllowedAsync(context, siteAId);

        Assert.False(allowed);
    }

    [Fact]
    public async Task HubConnection_WhenTheOriginBelongsToItsOwnSite_IsAllowed()
    {
        var (_, siteAId) = await SeedSiteAsync("https://site-a.example.com");

        var validator = CreateValidator();
        var context = BuildHubCallerContext("https://site-a.example.com");

        var allowed = await validator.IsAllowedAsync(context, siteAId);

        Assert.True(allowed);
    }

    [Fact]
    public async Task HubConnection_WhenNoOriginHeaderIsPresent_IsAllowed()
    {
        var (_, siteAId) = await SeedSiteAsync("https://site-a.example.com");

        var validator = CreateValidator();
        var context = BuildHubCallerContext(origin: null);

        var allowed = await validator.IsAllowedAsync(context, siteAId);

        Assert.True(allowed); // no cross-origin claim to verify
    }

    private async Task<(string PublicKey, SiteId SiteId)> SeedSiteAsync(string allowedOrigin)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var publicKey = $"site_{siteId.Value:N}";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, publicKey, [allowedOrigin]));
        await db.SaveChangesAsync();
        return (publicKey, siteId);
    }

    private HubOriginValidator CreateValidator() =>
        new(new GetSiteConfigByIdHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache()));

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

    private async Task<int> InvokeVisitorSessionAsync(string publicKey, string? origin)
    {
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());
        var rateLimiter = new AlwaysAllowRateLimiter();
        var rateLimitOptions = Options.Create(new VisitorSessionRateLimitOptions());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        if (origin is not null)
        {
            httpContext.Request.Headers.Origin = origin;
        }

        var result = await AuthEndpoints.HandleVisitorSessionAsync(
            new AuthEndpoints.VisitorSessionRequest(publicKey),
            getSite, new SiteInstallationSignalRepository(fixture.DataSource), rateLimiter, rateLimitOptions,
            new UuidV7Generator(), new SystemClock(), tokens, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);
        return httpContext.Response.StatusCode;
    }

    private static HubCallerContext BuildHubCallerContext(string? origin)
    {
        var features = new FeatureCollection();
        if (origin is not null)
        {
            var innerContext = new DefaultHttpContext();
            innerContext.Request.Headers.Origin = origin;
            // Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature - SignalR's own
            // HttpConnections-specific feature, not the generic ASP.NET Core hosting one (there is no
            // public concrete implementation to reuse, so a minimal one lives right here).
            features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(
                new SimpleHttpContextFeature(innerContext));
        }

        return new FakeHubCallerContext(features);
    }

    private sealed class SimpleHttpContextFeature(HttpContext httpContext) : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private sealed class AlwaysAllowRateLimiter : Ago.Platform.Abstractions.IRateLimiter
    {
        public Task<Ago.Platform.Abstractions.RateLimitDecision> CheckAsync(
            Ago.Platform.Abstractions.RateLimitKey key, Ago.Platform.Abstractions.RateLimitRule rule, CancellationToken cancellationToken) =>
            Task.FromResult(new Ago.Platform.Abstractions.RateLimitDecision(true, TimeSpan.Zero));
    }

    /// <summary>A minimal <see cref="HubCallerContext"/> carrying only what
    /// <see cref="HubOriginValidator"/> reads - <see cref="Features"/>, so a real
    /// <see cref="IHttpContextFeature"/> can supply an <c>Origin</c> header the way a genuine
    /// WebSocket upgrade request would.</summary>
    private sealed class FakeHubCallerContext(IFeatureCollection features) : HubCallerContext
    {
        public override string ConnectionId => "test-connection";

        public override string? UserIdentifier => null;

        public override System.Security.Claims.ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = features;

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
