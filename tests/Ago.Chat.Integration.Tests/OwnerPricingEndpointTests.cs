using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.GetPricingForOwner;
using Ago.Chat.Application.UseCases.PublishPriceVersion;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-20`'s own Done-when: "the screen is unreachable by anyone other than the platform owner,
/// proven by a test" - against a real Keycloak and a real Postgres (<see cref="OperatorOidcFixture"/>),
/// the identical shape <see cref="OwnerSitesEndpointTests"/> already established for the first owner
/// surface, reused here rather than re-invented: real tokens, the real `RequirePlatformOwner` policy,
/// the real `OwnerPricingEndpoints.MapOwnerPricingEndpoint`.
///
/// <para><b>Why this file still needs the full operator/DB scaffolding even though
/// <see cref="GetPricingForOwnerHandler"/> itself touches no database.</b>
/// <see cref="OperatorIdentityClaimsTransformation"/> runs for every authenticated request on this
/// host, Keycloak token or not, and resolves the caller's `sub` against `operators` through
/// <see cref="ResolveOperatorIdentityHandler"/> before any policy is evaluated - the identical
/// registration <see cref="OwnerSitesEndpointTests.BuildTestHostAsync"/> carries for the same reason,
/// copied here rather than trimmed, so this test host authenticates a request exactly as the real one
/// does.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerPricingEndpointTests(OperatorOidcFixture fixture)
{
    private const string Route = "/api/v1/owner/pricing";

    /// <summary>`25-29`: `ago-business` decision `0012`'s own real, decided numbers, not values this
    /// test invents - `490`/`200` replace `SeededPricePerSeatRub`'s own flat `590`, `0008`'s
    /// superseded grid. `ago-deploy/k8s/base/api.yaml` needs the matching manifest key rename
    /// (`Billing__PricePerSeatRub` -&gt; `Billing__BaseSeatPriceRub`/`Billing__PricePerExtraSeatRub`)
    /// in the same rollout as this change - a repository this item's own worktree cannot reach, so
    /// that update is this item's own report, not this file.</summary>
    private const decimal SeededBaseSeatPriceRub = 490m;

    private const decimal SeededPricePerExtraSeatRub = 200m;

    [Fact]
    public async Task OwnerToken_GetsThePriceList_WithTheRealSeatPricingNumbers()
    {
        await SeedSeatPricesAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerPricingResponse>();
        Assert.NotNull(body);

        // `25-29`: the legacy `PricePerSeatRub` field is kept on the wire but now reports the
        // marginal rate, not a flat per-seat price - see `OwnerSeatPricingDto`'s own remarks.
        Assert.Equal(SeededPricePerExtraSeatRub, body.SeatPricing.PricePerSeatRub);
        Assert.Equal(SubscriptionTierBands.BaseSeats, body.SeatPricing.BaseSeats);
        Assert.Equal(SeededBaseSeatPriceRub, body.SeatPricing.BaseSeatPriceRub);
        Assert.Equal(SeededPricePerExtraSeatRub, body.SeatPricing.PricePerExtraSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, body.SeatPricing.BillingPeriodDays);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, body.SeatPricing.FreeSeatsIncluded);

        // `25-29`: one tier, not two - `0012` prices exactly one Business band, replacing the
        // "Starter"/"Growth" pair this test used to assert against `0008`'s superseded grid.
        var starter = Assert.Single(body.SeatPricing.Tiers);
        Assert.Equal(SubscriptionTierBands.Starter, starter.Key);
        Assert.Equal(SubscriptionTierBands.MinSeats, starter.MinSeats);
        Assert.Equal(SubscriptionTierBands.MaxSeats, starter.MaxSeats);

        // `25-20`'s own honest finding, proven rather than merely documented: no billing option carries
        // a price anywhere in this codebase today, so the list this response carries is empty, not
        // fabricated - see GetPricingForOwnerHandler's own remarks for why.
        Assert.Empty(body.BillingOptions);
    }

    /// <summary>The Done-when's own words: unreachable by anyone other than the platform owner. An
    /// ordinary operator, with a real Keycloak-signed token and a real `operators` row, is refused -
    /// the identical case <see cref="OwnerSitesEndpointTests.OrdinaryOperatorToken_IsRejected"/> proves
    /// for the first owner surface.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    /// <summary>The case that matters most, restated for this surface: a site-wide `"Admin"` holding
    /// `site:configure` for their own tenant is still not the platform owner - a permission granted
    /// broadly inside one tenant does not become the ability to read the platform's own price list.
    /// </summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        var token = await fixture.GetDemoAdminAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route)).StatusCode);
    }

    /// <summary>`25-43`'s own write side, proven end to end over real HTTP: the platform owner can
    /// publish a new version for an already-registered key, and the very next read of
    /// <see cref="Route"/> reflects it - the wire proof that <see cref="GetPricingForOwnerHandler"/>
    /// and <see cref="PublishPriceVersionHandler"/> actually agree about what "currently effective"
    /// means, not just that each compiles against the same port in isolation.</summary>
    [Fact]
    public async Task OwnerToken_CanPublishANewPriceVersion_AndTheNextReadReflectsIt()
    {
        await SeedSeatPricesAsync();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var publishResponse = await client.PostAsJsonAsync(
            $"/api/v1/owner/prices/{SubscriptionTierBands.BaseSeatPriceKey.Value}/versions",
            new OwnerPricingEndpoints.PublishPriceVersionRequest(555m));
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<OwnerPricingEndpoints.PublishedPriceVersionResponse>();
        Assert.NotNull(published);
        Assert.Equal(555m, published!.AmountRub);

        var body = await (await client.GetAsync(Route)).Content.ReadFromJsonAsync<OwnerPricingResponse>();
        Assert.Equal(555m, body!.SeatPricing.BaseSeatPriceRub);
    }

    /// <summary>The first decision this whole item turns on, proven at the wire: the owner may only
    /// ever move the Rouble figure for a key code has already registered - never invent one from this
    /// surface. <see cref="PublishPriceVersionHandler"/>'s own <c>PricedResourceKeys.IsKnown</c> guard
    /// is what actually refuses this; this test proves it is reachable through the real route, not
    /// only through a direct handler call.</summary>
    [Fact]
    public async Task OwnerToken_CannotPublishAPriceForAnUnregisteredKey()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PostAsJsonAsync(
            "/api/v1/owner/prices/not-a-real-key/versions", new OwnerPricingEndpoints.PublishPriceVersionRequest(100m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The Done-when's own words, restated for the write route: unreachable by anyone other
    /// than the platform owner.</summary>
    [Fact]
    public async Task OrdinaryOperatorToken_CannotPublishAPriceVersion()
    {
        var token = await fixture.GetDemoOperatorAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/owner/prices/{SubscriptionTierBands.BaseSeatPriceKey.Value}/versions",
            new OwnerPricingEndpoints.PublishPriceVersionRequest(100m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>`25-43`: publishes fresh versions of both seat-pricing keys into the real Postgres this
    /// fixture shares across every test in this collection - the identical "publish once per test,
    /// same amounts this file's own constants document" discipline
    /// <c>SubscriptionRenewalJobTests.SeedCurrentSeatPricesAsync</c> already establishes, restated here
    /// since this file builds its own DI container from scratch rather than sharing that one.</summary>
    private async Task SeedSeatPricesAsync()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(fixture.DataSource).Options;
        await using var db = new AgoChatDbContext(options);
        var prices = new PriceCatalogRepository(db);

        var baseResource = await prices.GetByKeyAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.BaseSeatPriceKey);
        baseResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededBaseSeatPriceRub, DateTimeOffset.UtcNow);
        await prices.SaveAsync(baseResource, CancellationToken.None);

        var extraResource = await prices.GetByKeyAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.ExtraSeatPriceKey);
        extraResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededPricePerExtraSeatRub, DateTimeOffset.UtcNow);
        await prices.SaveAsync(extraResource, CancellationToken.None);
    }

    private static HttpClient CreateClient(WebApplication host, string? token)
    {
        var client = host.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>Mirrors <see cref="OwnerSitesEndpointTests.BuildTestHostAsync"/> exactly for the
    /// authentication/authorization scaffolding - see this file's own class remarks for why that
    /// scaffolding is not trimmed even though this route's own handler needs no database. The one real
    /// difference is the production registration this file exists to test:
    /// <see cref="GetPricingForOwnerHandler"/>/<see cref="OwnerPricingEndpoints.MapOwnerPricingEndpoint"/>,
    /// and the <see cref="BillingOptions"/> singleton it takes - constructed directly here (this test
    /// host builds its own DI container from scratch, not from `Billing:*` configuration) with the
    /// identical values <see cref="SeededBaseSeatPriceRub"/>/<see cref="SeededPricePerExtraSeatRub"/>
    /// document.</summary>
    private async Task<WebApplication> BuildTestHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();

        // `25-43`: the production registration for this route is now `IPriceCatalogRepository`, not a
        // plain `BillingOptions` instance - `SeedSeatPricesAsync` below publishes
        // `SeededBaseSeatPriceRub`/`SeededPricePerExtraSeatRub` into the real Postgres this fixture
        // already runs, the same "real container, no in-memory fake" bar every other file in this
        // project holds.
        builder.Services.AddScoped<IPriceCatalogRepository, PriceCatalogRepository>();
        builder.Services.AddScoped<GetPricingForOwnerHandler>();
        // `25-43`: the write side this file's own new test exercises - PublishPriceVersionHandler's
        // own registration, mirroring GetPricingForOwnerHandler's immediately above (IIdGenerator is
        // already registered below, alongside IClock).
        builder.Services.AddScoped<PublishPriceVersionHandler>();

        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Operator, options =>
            {
                options.MapInboundClaims = false;
                options.Authority = fixture.KeycloakAuthority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = OperatorOidcFixture.ClientId,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            // `Program.cs`'s own declaration, reproduced verbatim - the identical copy
            // OwnerSitesEndpointTests' own BuildTestHostAsync already carries.
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping - no duplicated route or policy decision.
        app.MapOwnerPricingEndpoint();

        await app.StartAsync();
        return app;
    }
}
