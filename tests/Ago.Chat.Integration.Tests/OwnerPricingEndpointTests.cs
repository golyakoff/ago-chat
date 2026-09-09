using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.GetPricingForOwner;
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

    /// <summary>The real, deployed number - not a value this test invents. `590m` mirrors
    /// `ago-deploy/k8s/base/api.yaml`'s own `Billing__PricePerSeatRub`, reproduced here as this test
    /// host's own configuration (a test host builds its own DI container, so it cannot read that
    /// manifest) rather than left as an arbitrary fixture value, so a reviewer can compare this test's
    /// assertion against the real deployment's own number directly.</summary>
    private const decimal SeededPricePerSeatRub = 590m;

    [Fact]
    public async Task OwnerToken_GetsThePriceList_WithTheRealSeatPricingNumbers()
    {
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();

        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerPricingResponse>();
        Assert.NotNull(body);

        Assert.Equal(SeededPricePerSeatRub, body.SeatPricing.PricePerSeatRub);
        Assert.Equal(BillingSubscription.PeriodLength.TotalDays, body.SeatPricing.BillingPeriodDays);
        Assert.Equal(SubscriptionTierBands.FreeSeatsIncluded, body.SeatPricing.FreeSeatsIncluded);

        Assert.Collection(
            body.SeatPricing.Tiers,
            starter =>
            {
                Assert.Equal(SubscriptionTierBands.Starter, starter.Key);
                Assert.Equal(SubscriptionTierBands.MinSeats, starter.MinSeats);
                Assert.Equal(SubscriptionTierBands.GrowthMinSeats - 1, starter.MaxSeats);
            },
            growth =>
            {
                Assert.Equal(SubscriptionTierBands.Growth, growth.Key);
                Assert.Equal(SubscriptionTierBands.GrowthMinSeats, growth.MinSeats);
                Assert.Equal(SubscriptionTierBands.MaxSeats, growth.MaxSeats);
            });

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
    /// identical value <see cref="SeededPricePerSeatRub"/> documents.</summary>
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

        // The production registration for this route: a plain `BillingOptions` instance, exactly the
        // shape `ChatModule` binds from `Billing:*` and hands to every handler that takes it - built
        // directly rather than through `IOptions<T>` binding, since this test host has no
        // `Billing:PricePerSeatRub` configuration to bind from.
        builder.Services.AddSingleton(new BillingOptions
        {
            PricePerSeatRub = SeededPricePerSeatRub,
            CheckoutReturnUrl = "https://office.test.invalid/settings/billing",
        });
        builder.Services.AddScoped<GetPricingForOwnerHandler>();

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
