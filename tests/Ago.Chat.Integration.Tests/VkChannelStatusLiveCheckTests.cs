using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Vk;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-175`: real HTTP proof that <c>VkChannelEndpoints.HandleStatusAsync</c> now re-verifies the
/// community token live on every read, the same parity `25-174` gave MAX
/// (<see cref="MaxChannelStatusLiveCheckTests"/>, this file's own direct template) - both files exist
/// because <see cref="ChannelStatusEndpointsTests"/>'s own remarks now explicitly scope VK's "connected"
/// case out of that shared file: VK's route no longer fits "whose routes never call any provider" the
/// moment this item ships.
///
/// <para>Two real HTTP boundaries are stood up per test: the operator-facing API host (a real
/// <c>TestServer</c>, mapping only <see cref="VkChannelEndpoints"/>'s three routes) and a fake VK host (a
/// real, ephemeral-port Kestrel instance - <see cref="VkApiClientTests"/>/<see cref="VkLiveTokenCheckTests"/>'s
/// own established technique) that <see cref="VkApiClient"/>'s own <see cref="HttpClient"/> is pointed at
/// instead of the real <c>https://api.vk.com/method</c>.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class VkChannelStatusLiveCheckTests(OperatorOidcFixture fixture)
{
    private const string Token = "vk-test-community-token-not-a-real-secret";
    private const string ApiVersion = "5.199";
    private static readonly DateTimeOffset RegisteredAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    // A fixed, throwaway 32-byte AES-256 key (never a real secret - `ChannelCredentialCipherOptions`'s
    // own remarks) shared between `SeedActiveCredentialAsync`'s encrypt and `BuildTestHostAsync`'s own
    // `IChannelCredentialCipher` registration, so `HandleStatusAsync`'s live `cipher.Decrypt` call
    // recovers the exact plaintext token this file seeded - the identical need
    // `MaxChannelStatusLiveCheckTests`' own inline cipher construction has for the identical reason.
    private static readonly ChannelCredentialCipher Cipher = new(
        new ChannelCredentialCipherOptions { CredentialEncryptionKey = Convert.ToBase64String(new byte[32]) });

    [Fact]
    public async Task GetVkStatus_WithAGoodToken_ReportsVerified()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, publicHandle: null);

        await using var vkHost = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } })));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, vkHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
        Assert.True(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Null(body.RefusalReason);
    }

    /// <summary>`25-175`'s own done-when this item must not regress: unlike MAX/Telegram, a verified VK
    /// read never writes anything back to storage - VK has nothing to backfill
    /// (<see cref="VkLiveCheckOutcome"/>'s own remarks). A dedicated negative test, not just an omission
    /// from the assertion list above: seeds <c>PublicHandle: null</c>, runs the identical good-token flow,
    /// and re-reads the row to prove it is still exactly as seeded.</summary>
    [Fact]
    public async Task GetVkStatus_WithAGoodToken_NeverWritesAPublicHandle()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, publicHandle: null);

        await using var vkHost = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } })));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, vkHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var reloaded = await db.ChannelCredentials.FindAsync(credential.Id);
        Assert.Null(reloaded!.PublicHandle);
    }

    [Fact]
    public async Task GetVkStatus_WithARevokedToken_ReportsVerifiedFalseWithVksOwnStatedReason()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, publicHandle: null);

        await using var vkHost = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { error = new { error_code = 5, error_msg = "User authorization failed" } })));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, vkHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.False(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Contains("5", body.RefusalReason);
        Assert.Contains("User authorization failed", body.RefusalReason);
    }

    /// <summary>The three-way split this item exists to add: an unreachable VK (or this deployment's own
    /// egress to it) is a distinct fact from a refusal - a tenant should wait and retry, not go get a new
    /// token. The fake host here answers, but only after this test's own bound (`VkLiveTokenCheck.Timeout`,
    /// 5 seconds) would already have elapsed - the same "actually make it slow" standard
    /// <see cref="VkLiveTokenCheckTests"/>/<see cref="MaxChannelStatusLiveCheckTests"/> hold themselves
    /// to.</summary>
    [Fact]
    public async Task GetVkStatus_WhenVkIsUnreachable_ReportsUnreachable_DistinctFromARefusal()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var credential = await SeedActiveCredentialAsync(siteId, publicHandle: null);

        await using var vkHost = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), requestAborted);
                return Results.Json(new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, vkHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Null(body.Verified);
        Assert.True(body.Unreachable);
        Assert.Null(body.RefusalReason);
    }

    [Fact]
    public async Task GetVkStatus_WithNoCredential_ReturnsNotConnected_AndNeverCallsVk()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var vkCalled = false;
        await using var vkHost = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () =>
            {
                vkCalled = true;
                return Results.Json(new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, vkHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VkChannelEndpoints.VkChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
        Assert.Null(body.Verified);
        Assert.False(body.Unreachable);
        Assert.False(vkCalled);
    }

    // ------------------------------------------------------------------------------------------
    // Shared setup
    // ------------------------------------------------------------------------------------------

    private static string Route(SiteId siteId) => $"/api/v1/sites/{siteId.Value}/channels/vk";

    /// <summary>A fresh <see cref="Site"/> per test - <see cref="ChannelStatusEndpointsTests"/>'s own
    /// remarks give the full reasoning (isolation from other tests sharing the same collection's
    /// unresettable Postgres container).</summary>
    private async Task<SiteId> CreateFreshSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<ChannelCredential> SeedActiveCredentialAsync(SiteId siteId, string? publicHandle)
    {
        // A fresh ProviderAccountId per call, not a shared literal - `ux_channel_credentials_kind_provideraccountid_active`
        // is a real production constraint (one active credential per VK community token, system-wide,
        // not scoped per site) and this collection's own Postgres container is shared and unresettable
        // across every test in this file (`CreateFreshSiteAsync`'s own remarks give the fuller reasoning
        // for the identical pattern already applied to `SiteId`).
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Vk,
            tokenCiphertext: Cipher.Encrypt(Token), webhookSecretHash: [5, 6, 7, 8], RegisteredAt,
            providerAccountId: $"vk-group-{Guid.NewGuid():N}", publicHandle: publicHandle);

        await using var db = fixture.CreateDbContext();
        db.ChannelCredentials.Add(credential);
        await db.SaveChangesAsync();

        return credential;
    }

    /// <summary>Grants `demo-admin` a second, purpose-built role holding `channel:manage` on
    /// <paramref name="siteId"/> - <see cref="ChannelStatusEndpointsTests"/>'s own remarks give the full
    /// reasoning for why this does not touch the shared `"Admin"` role instead.</summary>
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

    private async Task GrantChannelEntitlementAsync(SiteId siteId)
    {
        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.Vk);
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

    private sealed record FakeVkHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port standing in for VK's own API -
    /// <see cref="VkApiClientTests"/>/<see cref="VkLiveTokenCheckTests"/>'s own established technique,
    /// reused here rather than shared (this project's own convention: every file that needs this shape
    /// builds its own small host).</summary>
    private static async Task<FakeVkHost> BuildFakeVkHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new FakeVkHost(app, baseUrl);
    }

    private async Task<WebApplication> BuildTestHostAsync(SiteId siteId, string vkApiBaseUrl)
    {
        await GrantChannelEntitlementAsync(siteId);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // `ChannelEntitlementOptionKeys.For` + `ConfiguredBillingOptionEntitlementProvider`'s own
        // convention: the option key's own string doubles as the module key it grants - the identical
        // shorthand `ChannelStatusEndpointsTests`'/`MaxChannelStatusLiveCheckTests`' own test hosts
        // already use.
        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.Vk);
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(
                $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{optionKey.Value}", optionKey.Value),
        ]);

        builder.Services.AddRouting();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        // The production registrations `GetChannelCredentialStatusHandler` and `HandleStatusAsync`'s own
        // live-check step need.
        builder.Services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddSingleton<IBillingOptionEntitlementProvider, ConfiguredBillingOptionEntitlementProvider>();
        builder.Services.AddScoped<IModuleQuantityGrantStore, ModuleQuantityGrantStore>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddSingleton<IIdGenerator, UuidV7Generator>();
        builder.Services.AddScoped<GetChannelCredentialStatusHandler>();
        builder.Services.AddScoped<IChannelCredentialCipher>(_ => Cipher);
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        // `MapVkChannelEndpoints` maps `POST`/`DELETE` too - and, exactly as `ChannelStatusEndpointsTests`'/
        // `MaxChannelStatusLiveCheckTests`' own remarks found live, ASP.NET Core's minimal-API routing
        // infers every mapped delegate's own parameter sources for every endpoint the moment
        // `UseAuthorization()`'s `AuthorizationPolicyCache` first enumerates `EndpointDataSource.Endpoints`
        // at `app.StartAsync()` - so `HandleConnectAsync`/`HandleDisconnectAsync`'s own services have to
        // be resolvable here too, even though this file's own tests only ever call `GET`.
        builder.Services.AddScoped<Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler>();
        builder.Services.AddScoped<RevokeChannelCredentialHandler>();
        builder.Services.AddScoped<IWebhookSecretGenerator, WebhookSecretGenerator>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new VkBotApiOptions()));
        builder.Services.AddScoped(_ => new VkApiClient(new HttpClient { BaseAddress = new Uri(vkApiBaseUrl) }, ApiVersion));

        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();

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

        app.MapVkChannelEndpoints();

        await app.StartAsync();
        return app;
    }
}
