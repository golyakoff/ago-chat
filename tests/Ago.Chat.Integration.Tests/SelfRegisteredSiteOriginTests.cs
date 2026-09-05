using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Cors;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `10-04`: proves `5-01`'s two-layer CORS mechanism actually works for a site created through
/// `10-02`'s own real registration path, not only the pre-seeded fixtures `OriginAuthorizationTests`/
/// `SiteOriginCorsPolicyProviderTests` use - those always seeded a site directly into Postgres *before*
/// ever touching the CORS cache, so neither of them had a reason to prove there is no gap between "the
/// registration transaction committed" and "both CORS layers recognise the row." This class calls the
/// real `RegisterSiteHandler` (matching `OriginAuthorizationTests`' own style of calling handlers
/// directly against real Postgres + real Redis, not standing up a `TestServer` - `RegisterSiteHandler`
/// itself needs no Keycloak token, only the `sub` string a validated one would carry, so
/// `OperatorOidcFixture`'s heavier Keycloak container is not needed here the way `SiteRegistrationTests`
/// needs it).
///
/// Also answers the negative-cache timing question `5-01`'s own TTL-only invalidation note raises
/// (`docs/backlog/10-04-...md`, `docs/backlog/5-01-per-site-cors.md`): could a request that checked an
/// origin *before* a site claiming it existed strand a real new self-registered site behind a cached
/// "no site allows this origin" answer until `CheckCorsOriginHandler`'s 30-second negative TTL expires?
///
/// <b>Answer: not for self-registration specifically.</b> The only way `CheckCorsOriginHandler`'s
/// per-origin cache key (`cors-origin:{origin}`) can hold a stale negative for a soon-to-be-registered
/// origin is if some earlier request actually carried that *exact* `Origin` header before the site
/// existed. For a self-registered site that is not possible by construction:
/// - The origin is a value the registering user types into `10-03`'s signup form and only becomes
///   `Site.AllowedOrigins` once `RegisterSiteHandler` commits it - nothing upstream of that moment knows
///   the value, so nothing upstream of that moment can have sent it as an `Origin` header.
/// - The registration call itself (`POST /api/v1/sites`) is made by the signup form's own JavaScript,
///   from the console's origin - never from the customer's own site - so it cannot poison the cache key
///   for the origin being registered either.
/// - The one caller who *could* type that origin into a request early - the registering user - has no
///   reason to and no way to before signing up: they have no public key yet to hand to
///   `POST /api/v1/visitor-sessions`, and the widget script that would make their own site's browser
///   send that `Origin` header does not exist on their page until they embed it, which requires the
///   public key `10-02`'s response gives them *after* registration.
/// - Editing or re-claiming an origin after signup is explicitly out of this item's scope (`10-04`'s own
///   "Out of scope"), so there is no path where an origin gets deliberately vacated and immediately
///   re-claimed by a different site either - the one scenario that could otherwise matter.
///
/// <see cref="RegisterThenImmediateWidgetHandshake_FromTheRegisteredOrigin_PassesBothCorsLayers"/> is
/// the concrete proof: the origin used is unique per test run (never checked by anything before this
/// test registers it), and both CORS layers accept it immediately after registration with no wait, no
/// retry, and no cache warm-up step - exactly the real flow a freshly onboarded customer's widget
/// embed would follow.
///
/// <see cref="AnOriginCheckedBeforeAnySiteClaimsIt_StaysNegativelyCachedThroughARaceRegistration"/>
/// documents the mechanism's actual (already-known, `caching.md`) TTL-only-invalidation limitation
/// directly, by deliberately doing the one thing the reasoning above says no real self-registration
/// caller ever does - checking the origin before the site exists - so the boundary is demonstrated, not
/// just asserted in prose. No fix was made because there is no reachable gap: nothing in this codebase
/// exercises the pre-check this test performs by hand.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class SelfRegisteredSiteOriginTests(SiteCachingFixture fixture)
{
    [Fact]
    public async Task RegisterThenImmediateWidgetHandshake_FromTheRegisteredOrigin_PassesBothCorsLayers()
    {
        // Unique per test run - the whole point is that nothing has ever checked this exact origin
        // before, matching how a real customer's freshly-chosen domain has never been seen either.
        var origin = $"https://{Guid.NewGuid():N}.example.com";
        var (publicKey, _) = await RegisterSiteAsync(origin);

        // Layer 1 - the CORS policy itself: this is the first thing that ever touches
        // "cors-origin:{origin}" in Redis, immediately after the registration transaction committed.
        var corsPolicy = await CheckCorsPolicyAsync(origin);
        Assert.NotNull(corsPolicy);
        Assert.Contains(origin, corsPolicy.Origins);

        // Layer 2 - the real per-site boundary: a widget handshake from that exact origin, naming the
        // site's own freshly-minted public key.
        var status = await InvokeVisitorSessionAsync(publicKey, origin);
        Assert.Equal(StatusCodes.Status201Created, status);
    }

    [Fact]
    public async Task AnOriginCheckedBeforeAnySiteClaimsIt_StaysNegativelyCachedThroughARaceRegistration()
    {
        // Deliberately the scenario the reasoning above rules out for any real caller: something
        // checks this origin while it belongs to no site, forcing a negative cache entry, and then a
        // site claims that exact origin a moment later - still well inside the 30s negative TTL
        // (`CheckCorsOriginHandler.NegativeOptions`).
        var origin = $"https://{Guid.NewGuid():N}.example.com";

        var beforeRegistration = await CheckCorsPolicyAsync(origin);
        Assert.Null(beforeRegistration); // no site claims it yet - correctly denied, and now cached as such

        await RegisterSiteAsync(origin);

        // The known, already-documented limitation (`caching.md`: "TTL only" for this data): a stale
        // negative answer outlives the site that would now make it a positive one, until the 30s
        // window elapses. This is not a bug this item introduces or needs to fix - it is the existing
        // `5-01` mechanism behaving exactly as its own note says, demonstrated here rather than only
        // described, and reachable only by a check this specific test had to perform by hand.
        var immediatelyAfterRegistration = await CheckCorsPolicyAsync(origin);
        Assert.Null(immediatelyAfterRegistration);
    }

    private async Task<(string PublicKey, SiteId SiteId)> RegisterSiteAsync(string origin)
    {
        var registrationDb = fixture.CreateDbContext();
        var handler = new RegisterSiteHandler(
            new SiteRegistrationRepository(
                registrationDb, new EfOutboxWriter<AgoChatDbContext>(registrationDb), new UuidV7Generator(), new SystemClock()),
            new FakeRateLimiter(),
            new RegisterSiteRateLimitOptions(),
            new UuidV7Generator(),
            new SystemClock());

        var command = new RegisterSite(
            ExternalSubjectId: $"keycloak-sub-{Guid.NewGuid():N}", RequestIp: "203.0.113.9", SiteName: "Freshly Registered", origin);

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Code : null);

        await using var db = fixture.CreateDbContext();
        var site = await db.Sites.SingleAsync(s => s.Id == new SiteId(result.Value.SiteId));
        return (site.PublicKey, site.Id);
    }

    private async Task<Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicy?> CheckCorsPolicyAsync(string origin)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Ago.Chat.Application.Abstractions.ISiteRepository>(new SiteRepository(fixture.CreateDbContext()));
        services.AddSingleton<Ago.Platform.Abstractions.ICache>(CreateCache());
        services.AddScoped<CheckCorsOriginHandler>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Headers.Origin = origin;

        var provider = new SiteOriginCorsPolicyProvider();
        return await provider.GetPolicyAsync(context, policyName: null);
    }

    private RedisCache CreateCache() => new(
        fixture.RedisMultiplexer, new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(), NullLogger<RedisCache>.Instance);

    private async Task<int> InvokeVisitorSessionAsync(string publicKey, string origin)
    {
        var getSite = new GetSiteConfigByPublicKeyHandler(new SiteRepository(fixture.CreateDbContext()), CreateCache());
        var tokens = new JwtTokenService(TestSigningKeys.Ring(), "test-issuer", new SystemClock());
        var rateLimiter = new FakeRateLimiter();
        var rateLimitOptions = Options.Create(new VisitorSessionRateLimitOptions());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        httpContext.Request.Headers.Origin = origin;

        var result = await AuthEndpoints.HandleVisitorSessionAsync(
            new AuthEndpoints.VisitorSessionRequest(publicKey),
            getSite, new SiteInstallationSignalRepository(fixture.DataSource), rateLimiter, rateLimitOptions,
            new UuidV7Generator(), new SystemClock(), tokens, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);
        return httpContext.Response.StatusCode;
    }
}
