using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ago.Chat.Api.Auth;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `17-08`/`adr/0048`: <c>POST /api/v1/visitor-sessions/renew</c>, against real Postgres and real
/// Redis (<see cref="SiteCachingFixture"/> - the same combination the mint's own tests need, since
/// the site lookup is cached).
///
/// Everything here goes through a real <see cref="WebApplication"/> that calls the production
/// <c>AuthEndpoints.MapAuthEndpoints</c>, not a hand-rolled copy of the route: the two cases that
/// matter most - an expired token and an anonymous caller - are decided by the Visitor JWT scheme and
/// the route's own <c>RequireAuthorization</c> before the handler is reached at all, so a test that
/// invoked the handler directly (the seam <see cref="RateLimitingTests"/> uses for the mint) could
/// not observe them. The tokens are real ones minted by <see cref="JwtTokenService"/> and validated
/// by a real JWT bearer handler; the expired one is expired because it was issued by a clock set
/// eight days in the past, not because a claim was hand-edited.
///
/// The scheme's own configuration is transcribed from `Program.cs`, the same way
/// <see cref="TokenSchemeSeparationTests"/> and <see cref="SiteRegistrationTests"/> already
/// transcribe theirs - standing up the whole host would drag in RabbitMQ, MinIO and Keycloak to prove
/// something about one endpoint.
/// </summary>
[Collection(SiteCachingCollection.Name)]
public sealed class VisitorSessionRenewalTests(SiteCachingFixture fixture)
{
    private const string Issuer = "ago-chat-api";

    // `17-03`: a key *ring* rather than a bare SigningCredentials, since that is what
    // JwtTokenService takes now. One active key and nothing retired - rotation is not this file's
    // subject (VisitorKeyRotationTests is).
    private readonly VisitorSigningKeyRing _signingKeys = TestSigningKeys.Ring();

    /// <summary>
    /// The point of the whole item: the visitor comes out the other side as the *same* visitor. A
    /// re-mint through the public endpoint would also return a working token and would lose the
    /// conversation, which is why "200 with a new token" is only half the assertion.
    ///
    /// The token being renewed is six days old - i.e. inside its renewal window, which is the only
    /// state `ago-widget` ever calls this from. Renewing a token minted in the same *second* is a
    /// no-op by construction (`exp` has one-second granularity, and every other claim is unchanged),
    /// so a test that renewed a brand-new token and asserted "a different string came back" would be
    /// asserting the wall clock rather than the endpoint.
    /// </summary>
    [Fact]
    public async Task ARenewal_Returns200_WithAFullFreshLifetimeForTheSameVisitor()
    {
        var site = await SeedSiteAsync();
        var visitorId = new VisitorId(Guid.NewGuid());
        var nearlyExpired = IssueToken(visitorId, site.SiteId, MintedDaysAgo(6));

        await using var app = await BuildAppAsync();
        var response = await RenewAsync(app, nearlyExpired, site.PublicKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthEndpoints.VisitorSessionResponse>();
        Assert.NotNull(body);
        Assert.Equal(visitorId.Value, body.VisitorId);

        // A full fresh lifetime, not the remaining day: this is what makes renewal sliding
        // (`adr/0048`) rather than a way to squeeze a little more out of one token. Compared against
        // the *old* token's own expiry rather than against a wall clock, so the assertion says
        // "further out than the one it replaced" without depending on when the test ran.
        var handler = new JwtSecurityTokenHandler();
        var expiryBefore = handler.ReadJwtToken(nearlyExpired).ValidTo;
        var expiryAfter = handler.ReadJwtToken(body.Token).ValidTo;
        Assert.True(
            expiryAfter - expiryBefore > TimeSpan.FromDays(5),
            $"expected a full fresh lifetime, moved from {expiryBefore:O} to {expiryAfter:O}");

        // Not just "some token came back" - the new one authenticates, for the same visitor, on the
        // same scheme. A renewal that returned an unusable string would pass every assertion above.
        var second = await RenewAsync(app, body.Token, site.PublicKey);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            visitorId.Value,
            (await second.Content.ReadFromJsonAsync<AuthEndpoints.VisitorSessionResponse>())!.VisitorId);
    }

    /// <summary>
    /// `17-08`'s other half: <c>JwtTokenService.VisitorTokenLifetime</c> is seven days, and it is
    /// asserted here rather than only next to the constant, because the number that matters is the
    /// one that reaches the wire - it is what `ago-widget` divides by three to find its own renewal
    /// window (`adr/0048`: read from the token, never from a client-side copy of this constant), and
    /// what `17-03`'s key-rotation drain window is derived from.
    /// </summary>
    [Fact]
    public async Task TheTokenARenewalIssues_LastsSevenDays()
    {
        var site = await SeedSiteAsync();
        var token = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(6));

        await using var app = await BuildAppAsync();
        var body = await (await RenewAsync(app, token, site.PublicKey))
            .Content.ReadFromJsonAsync<AuthEndpoints.VisitorSessionResponse>();

        var renewed = new JwtSecurityTokenHandler().ReadJwtToken(body!.Token);
        Assert.Equal(TimeSpan.FromDays(7), renewed.ValidTo - renewed.ValidFrom);
    }

    /// <summary>
    /// `adr/0048`: "the handler rejects a request whose resolved `SiteId` does not match the token's
    /// claim." `403`, not `404` - the site in the body is perfectly real, it is simply not this
    /// token's. `ago-widget` reads `403` as definitive and ends the session, which is the correct
    /// answer for an embed presenting another tenant's key.
    /// </summary>
    [Fact]
    public async Task ATokenForOneSite_CannotBeRenewedByPresentingAnotherSitesPublicKey()
    {
        var siteA = await SeedSiteAsync();
        var siteB = await SeedSiteAsync();
        var token = IssueToken(new VisitorId(Guid.NewGuid()), siteA.SiteId, MintedDaysAgo(0));

        await using var app = await BuildAppAsync();
        var response = await RenewAsync(app, token, siteB.PublicKey);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Decided by the scheme, before the handler runs. `401` rather than `403` matters: the widget
    /// treats both as "this identity is finished", but only a `401` means the token never
    /// authenticated at all, which is what an expired credential should look like.
    /// </summary>
    [Fact]
    public async Task AnExpiredToken_IsRejected()
    {
        var site = await SeedSiteAsync();
        // Eight days ago, against a seven-day lifetime - expired by a day, far outside the bearer
        // handler's five-minute default clock skew.
        var token = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(8));

        await using var app = await BuildAppAsync();
        var response = await RenewAsync(app, token, site.PublicKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The mint is public by design; this endpoint must not be. Without the token there is no `sub`
    /// to renew and no `site_id` to check the body against - which is exactly why `adr/0048` refused
    /// to make renewal a flag on the mint.
    /// </summary>
    [Fact]
    public async Task AnUnauthenticatedCall_IsRejected()
    {
        var site = await SeedSiteAsync();

        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/visitor-sessions/renew", new AuthEndpoints.VisitorSessionRequest(site.PublicKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A real Redis token bucket with capacity 1 and a refill slow enough that a second
    /// immediate call cannot have refilled - the limiter actually denying, not merely being
    /// constructed. <c>Retry-After</c> is asserted because `ago-widget` honours it: without the header
    /// it falls back to its own jittered backoff, so a missing header is a silent degradation rather
    /// than a visible break.</summary>
    [Fact]
    public async Task OnceThePerVisitorBucketIsExhausted_Returns429WithARetryAfterHeader()
    {
        var site = await SeedSiteAsync();
        var token = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(0));

        await using var app = await BuildAppAsync(new VisitorSessionRenewalRateLimitOptions
        {
            PerVisitorCapacity = 1,
            PerVisitorRefillPerSecond = 0.001,
        });

        var first = await RenewAsync(app, token, site.PublicKey);
        var second = await RenewAsync(app, token, site.PublicKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Null(first.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
    }

    /// <summary>
    /// `adr/0048`'s deliberate deviation from the mint's shape, and the half of it a "the limit
    /// limits" test cannot see: the key is the *visitor*, not the site. A per-site bucket would let
    /// one abusive token-holder lock out every honest visitor on the same shop - so the second
    /// visitor here, on the same site as the one that just exhausted its bucket, must still be served.
    /// </summary>
    [Fact]
    public async Task TheBucketIsPerVisitor_SoOneVisitorExhaustingItDoesNotBlockAnotherOnTheSameSite()
    {
        var site = await SeedSiteAsync();
        var firstVisitor = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(0));
        var secondVisitor = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(0));

        await using var app = await BuildAppAsync(new VisitorSessionRenewalRateLimitOptions
        {
            PerVisitorCapacity = 1,
            PerVisitorRefillPerSecond = 0.001,
        });

        Assert.Equal(HttpStatusCode.OK, (await RenewAsync(app, firstVisitor, site.PublicKey)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RenewAsync(app, firstVisitor, site.PublicKey)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await RenewAsync(app, secondVisitor, site.PublicKey)).StatusCode);
    }

    /// <summary>`5-01` layer 2, wired on this route too rather than only on the mint - CORS proved the
    /// origin belongs to *some* site, and this proves it belongs to this one.</summary>
    [Fact]
    public async Task AnOriginBelongingToADifferentSite_IsRejected()
    {
        var site = await SeedSiteAsync(allowedOrigin: "https://site-a.example.com");
        await SeedSiteAsync(allowedOrigin: "https://site-b.example.com");
        var token = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(0));

        await using var app = await BuildAppAsync();
        var response = await RenewAsync(app, token, site.PublicKey, origin: "https://site-b.example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The reason `adr/0048` insisted the response reuse the mint's shape: it closes the limitation
    /// `ago-widget/src/storage.ts` has recorded since `11-03` - "needs a session endpoint that can
    /// return current config without minting a new visitor". A returning visitor's cached appearance
    /// is now at most one renewal window stale rather than frozen at first mint.
    /// </summary>
    [Fact]
    public async Task ARenewal_ReturnsTheSitesCurrentWidgetConfig_NotTheOneInEffectWhenTheVisitorWasMinted()
    {
        var site = await SeedSiteAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Sites.SingleAsync(s => s.Id == new SiteId(site.SiteId));
            row.UpdateWidgetConfig(new WidgetConfig("#ff8800", Position.BottomLeft), DateTimeOffset.UtcNow);
            row.UpdateLocale(Locale.Ru, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var token = IssueToken(new VisitorId(Guid.NewGuid()), site.SiteId, MintedDaysAgo(0));

        await using var app = await BuildAppAsync();
        var body = await (await RenewAsync(app, token, site.PublicKey))
            .Content.ReadFromJsonAsync<AuthEndpoints.VisitorSessionResponse>();

        Assert.Equal("#ff8800", body!.WidgetPrimaryColorHex);
        Assert.Equal(nameof(Position.BottomLeft), body.WidgetPosition);
        Assert.Equal(nameof(Locale.Ru), body.WidgetLocale);
    }

    private static Task<HttpResponseMessage> RenewAsync(
        WebApplication app, string token, string publicKey, string? origin = null)
    {
        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/visitor-sessions/renew")
        {
            Content = JsonContent.Create(new AuthEndpoints.VisitorSessionRequest(publicKey)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return client.SendAsync(request);
    }

    private string IssueToken(VisitorId visitorId, Guid siteId, DateTimeOffset mintedAt) =>
        new JwtTokenService(_signingKeys, Issuer, new FixedClock(mintedAt))
            .IssueVisitorToken(visitorId, new SiteId(siteId));

    private static DateTimeOffset MintedDaysAgo(double days) => DateTimeOffset.UtcNow.AddDays(-days);

    private async Task<(Guid SiteId, string PublicKey)> SeedSiteAsync(string? allowedOrigin = null)
    {
        var siteId = new SiteId(Guid.NewGuid());
        // A fresh key per seeded site, so the cached site lookup can never serve one test's site to
        // another's - the cache is a real, shared Redis for the whole collection.
        var publicKey = $"site_{siteId.Value:N}";
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, publicKey, allowedOrigin is null ? [] : [allowedOrigin]));
        await db.SaveChangesAsync();
        return (siteId.Value, publicKey);
    }

    /// <summary>The real production route mapping (<c>MapAuthEndpoints</c>) on a real
    /// <see cref="WebApplication"/>, the seam <see cref="SiteRegistrationTests"/> established for
    /// endpoint files typed against <see cref="WebApplication"/>.</summary>
    private async Task<WebApplication> BuildAppAsync(VisitorSessionRenewalRateLimitOptions? renewalLimits = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        builder.Services.AddScoped<GetSiteConfigByPublicKeyHandler>();
        builder.Services.AddSingleton<ICache>(new RedisCache(
            fixture.RedisMultiplexer,
            new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
            NullLogger<RedisCache>.Instance));
        // A real Redis bucket, not FakeRateLimiter - the 429 case is one of this file's own reasons
        // to exist, and an always-allow limiter would make it unprovable.
        builder.Services.AddSingleton<IRateLimiter>(new RedisRateLimiter(
            fixture.RedisMultiplexer,
            new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(2)).Build(),
            NullLogger<RedisRateLimiter>.Instance));
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        builder.Services.AddSingleton(sp => new JwtTokenService(
            _signingKeys, Issuer, sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new VisitorSessionRateLimitOptions()));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            renewalLimits ?? new VisitorSessionRenewalRateLimitOptions()));

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Visitor, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = JwtSchemes.Visitor,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, _, _, _) => _signingKeys.ValidationKeys(),
                    ValidateLifetime = true,
                };
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAuthEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
