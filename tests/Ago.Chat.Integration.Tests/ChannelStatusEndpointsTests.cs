using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Vk;
using Ago.Chat.Infrastructure.WhatsApp;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-65`: real HTTP proof for the `GET` status route each of `VkChannelEndpoints`,
/// `WhatsAppChannelEndpoints` and `AvitoChannelEndpoints` gained in this item - before it, only `POST`
/// (connect) and `DELETE` (disconnect) existed on any of the three. `TelegramChannelEndpoints`'s own
/// live-check status route and `MaxChannelEndpoints`'s own non-live one (the shape this item's three
/// routes actually copy) have no HTTP-level test of their own anywhere in this suite - both are proven
/// only at the `GetChannelCredentialStatusHandler` level
/// (`Ago.Chat.Application.Tests.UseCases.GetChannelCredentialStatus.GetChannelCredentialStatusHandlerTests`),
/// which is real coverage of the handler's own orchestration but proves nothing about the route mapping,
/// the response's own JSON shape, or the permission/entitlement gate actually reached over real HTTP for
/// these three specific classes. This file is deliberately the first of either kind for any channel -
/// see this item's own worker report for why that gap in the existing suite is worth naming rather than
/// silently matched.
///
/// <para><b>Only the `GET` route of each group is ever dispatched to by this file's own tests, but every
/// route's own services still have to be registered.</b> `MapVkChannelEndpoints`/
/// `MapWhatsAppChannelEndpoints`/`MapAvitoChannelEndpoints` each map `POST`/`DELETE` too - found live
/// while writing this file: `UseAuthorization()`'s own `AuthorizationPolicyCache` enumerates every
/// mapped endpoint at `app.StartAsync()` (not lazily per request, this file's own first assumption), and
/// that enumeration infers every mapped delegate's own parameter sources for metadata, regardless of
/// which route a test actually calls. So `VkApiClient`/`WhatsAppApiClient`/`AvitoApiClient`,
/// `RegisterChannelCredentialHandler`/`RevokeChannelCredentialHandler` and their own remaining
/// dependencies are all registered in <see cref="BuildTestHostAsync"/> below, real production types
/// throughout (never test doubles - nothing here is ever actually invoked, so a fake would prove
/// nothing either way, and the real types cost nothing extra since each already depends only on ports
/// this file registers anyway for the status handler).</para>
///
/// <para><b>Why `ChannelManage` is granted through a second, purpose-built role rather than added to
/// the shared `"Admin"` role `OperatorOidcFixture.InitializeAsync` seeds.</b> That role was seeded before
/// `23-36` added `channel:manage` to `RegisterSiteHandler.AdminRolePermissions` - a real, separate gap
/// this file's own investigation found (a fixture drifted out of sync with the production default it is
/// meant to model) but is out of `25-65`'s own scope to fix, and editing a role every other test file in
/// this shared collection also reads from is a wider blast radius than one item's own additive test
/// needs to take. A second role, scoped to <see cref="OperatorOidcFixture.SeededSiteId"/> and bound only
/// to <see cref="OperatorOidcFixture.SeededAdminOperatorId"/>, adds a permission without touching or
/// removing any other test's own assumption about what `"Admin"` holds.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class ChannelStatusEndpointsTests(OperatorOidcFixture fixture)
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------------------
    // VK
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetVkStatus_WithAnActiveCredential_ReturnsTheRealPersistedConnectedStatus()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, ChannelKind.Vk, "vk-provider-account-1");

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.Vk);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.Vk));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
    }

    [Fact]
    public async Task GetVkStatus_WithNoCredential_ReturnsNotConnected()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.Vk);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.Vk));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
    }

    // ------------------------------------------------------------------------------------------
    // WhatsApp
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetWhatsAppStatus_WithAnActiveCredential_ReturnsTheRealPersistedConnectedStatus()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, ChannelKind.WhatsApp, "whatsapp-phone-number-id-1");

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.WhatsApp);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.WhatsApp));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
    }

    [Fact]
    public async Task GetWhatsAppStatus_WithNoCredential_ReturnsNotConnected()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.WhatsApp);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.WhatsApp));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
    }

    // ------------------------------------------------------------------------------------------
    // Avito
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAvitoStatus_WithAnActiveCredential_ReturnsTheRealPersistedConnectedStatus()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, ChannelKind.Avito, "avito-user-id-1");

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.Avito);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.Avito));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
    }

    [Fact]
    public async Task GetAvitoStatus_WithNoCredential_ReturnsNotConnected()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, ChannelKind.Avito);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId, ChannelKind.Avito));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
    }

    // ------------------------------------------------------------------------------------------
    // Shared setup
    // ------------------------------------------------------------------------------------------

    private static string Route(SiteId siteId, ChannelKind kind) =>
        $"/api/v1/sites/{siteId.Value}/channels/{kind.ToString().ToLowerInvariant()}";

    /// <summary>A fresh <see cref="Site"/> per test, not the shared <see cref="OperatorOidcFixture.SeededSiteId"/>
    /// every other file in this collection also reads and writes - `ModuleEndpointsTests`' own
    /// per-test-site precedent (`victimSiteId`), needed here for a second reason beyond isolation from
    /// other files: `GetChannelCredentialStatusHandler` reads the *active* credential for a
    /// `(SiteId, ChannelKind)` pair, so two tests in this same file sharing one site and one
    /// `ChannelKind.Vk` (the "connected" test and the "not connected" test) would otherwise see each
    /// other's writes depending on which the collection's shared, unresettable Postgres container
    /// happens to run first - found live while writing this file (`GetVkStatus_WithNoCredential_ReturnsNotConnected`
    /// failed only when run after its own "connected" sibling had already seeded a row).</summary>
    private async Task<SiteId> CreateFreshSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<ChannelCredential> SeedActiveCredentialAsync(SiteId siteId, ChannelKind kind, string providerAccountId)
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, kind,
            tokenCiphertext: [1, 2, 3, 4], webhookSecretHash: [5, 6, 7, 8], RegisteredAt,
            providerAccountId: providerAccountId);

        await using var db = fixture.CreateDbContext();
        db.ChannelCredentials.Add(credential);
        await db.SaveChangesAsync();

        return credential;
    }

    /// <summary>Grants `demo-admin` a second, `25-65`-only role holding `channel:manage` on
    /// <paramref name="siteId"/> - see this class's own remarks for why this does not touch the shared
    /// `"Admin"` role instead. A fresh role id per call, so running more than one test in this file
    /// never collides on a unique index.</summary>
    private async Task GrantChannelManageAsync(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        var roleId = Guid.NewGuid();
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = $"channel-manage-test-{roleId:N}",
            Permissions = [Permission.ChannelManage.Value],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = fixture.SeededAdminOperatorId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    /// <summary>Grants the site an effective quantity of 1 for the billing option
    /// `ChannelEntitlementOptionKeys.For(kind)` maps to - `ChannelEntitlement.IsEntitledAsync`'s own two
    /// steps, both satisfied here: <see cref="BuildTestHostAsync"/> configures
    /// `ConfiguredBillingOptionEntitlementProvider` to map that option key to a `ModuleKey` of the
    /// identical string (the same convention
    /// `GetChannelCredentialStatusHandlerTests.CreateFixture`'s own fake entitlement setup uses), and
    /// this grants that module key a real, database-backed quantity through the production
    /// `ModuleQuantityGrantStore` - not a fake, so this test proves the real entitlement gate passes,
    /// not merely that a test double was configured to let it.</summary>
    private async Task GrantChannelEntitlementAsync(SiteId siteId, ChannelKind kind)
    {
        var optionKey = ChannelEntitlementOptionKeys.For(kind);
        await using var db = fixture.CreateDbContext();
        var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock());
        await grants.GrantAsync(siteId, new ModuleKey(optionKey.Value), 1, RegisteredAt, CancellationToken.None);
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

    private async Task<WebApplication> BuildTestHostAsync(SiteId siteId, ChannelKind entitledKind)
    {
        await GrantChannelEntitlementAsync(siteId, entitledKind);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // `ChannelEntitlementOptionKeys.For` + `ConfiguredBillingOptionEntitlementProvider`'s own
        // convention: the option key's own string doubles as the module key it grants, the identical
        // shorthand `GetChannelCredentialStatusHandlerTests`' own fake entitlement provider uses.
        builder.Configuration.AddInMemoryCollection(
            Enum.GetValues<ChannelKind>().Select(kind =>
            {
                var optionKey = ChannelEntitlementOptionKeys.For(kind);
                return new KeyValuePair<string, string?>(
                    $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{optionKey.Value}", optionKey.Value);
            }));

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        // The production registrations `GetChannelCredentialStatusHandler` needs, exactly as
        // ChatModule/AddPostgresPersistence make them.
        builder.Services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddSingleton<IBillingOptionEntitlementProvider, ConfiguredBillingOptionEntitlementProvider>();
        builder.Services.AddScoped<IModuleQuantityGrantStore, ModuleQuantityGrantStore>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddScoped<GetChannelCredentialStatusHandler>();

        // `MapVkChannelEndpoints`/`MapWhatsAppChannelEndpoints`/`MapAvitoChannelEndpoints` each map
        // `POST`/`DELETE` too - and, found live while writing this file, ASP.NET Core's minimal-API
        // routing infers every mapped delegate's own parameter sources for every endpoint the moment
        // `UseAuthorization()`'s `AuthorizationPolicyCache` first enumerates `EndpointDataSource.Endpoints`
        // (at `app.StartAsync()` below, not lazily per request the way this file originally assumed) -
        // so the connect/disconnect handlers' own services have to be resolvable here too, even though
        // this file's own tests only ever call `GET`. Real production types throughout, not test
        // doubles: nothing here is ever actually invoked (no test posts or deletes), so a fake would
        // prove nothing either way, and the real types cost nothing extra to wire since every one of
        // them already depends on ports this file registers above for the status handler anyway.
        builder.Services.AddScoped<Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler>();
        builder.Services.AddScoped<Application.UseCases.RevokeChannelCredential.RevokeChannelCredentialHandler>();
        builder.Services.AddScoped<IChannelCredentialCipher>(_ => new ChannelCredentialCipher(
            new ChannelCredentialCipherOptions { CredentialEncryptionKey = Convert.ToBase64String(new byte[32]) }));
        builder.Services.AddScoped<IWebhookSecretGenerator, WebhookSecretGenerator>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new VkBotApiOptions()));
        builder.Services.AddScoped(_ => new VkApiClient(new HttpClient(), new VkBotApiOptions().ApiVersion));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WhatsAppBotApiOptions()));
        builder.Services.AddScoped(_ => new WhatsAppApiClient(new HttpClient()));
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AvitoApiOptions()));
        builder.Services.AddScoped(_ => new AvitoApiClient(new HttpClient()));

        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ISiteActivityWatchdog, SiteActivityWatchdogRepository>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SiteActivityWatchdogOptions()));
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
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The real production mapping for all three - only each group's own `GET` route is ever
        // dispatched to in this file's tests; see this class's own remarks for why `POST`/`DELETE`'s
        // extra services need no registration here.
        app.MapVkChannelEndpoints();
        app.MapWhatsAppChannelEndpoints();
        app.MapAvitoChannelEndpoints();

        await app.StartAsync();
        return app;
    }
}
