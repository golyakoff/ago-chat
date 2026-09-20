using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Channels;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.WhatsApp;
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
/// `25-176`: real HTTP proof that <c>WhatsAppChannelEndpoints.HandleStatusAsync</c> now re-verifies the
/// (token, phone_number_id) pair live on every read, the same parity `25-174` gave MAX
/// (<see cref="MaxChannelStatusLiveCheckTests"/>, this file's own direct template for the backfill
/// assertion - WhatsApp is in MAX's "has a handle to backfill" camp, not VK's) - both files exist because
/// <see cref="ChannelStatusEndpointsTests"/>'s own remarks now explicitly scope WhatsApp's "connected"
/// case out of that shared file: WhatsApp's route no longer fits "whose routes never call any provider"
/// the moment this item ships.
///
/// <para>Two real HTTP boundaries are stood up per test: the operator-facing API host (a real
/// <c>TestServer</c>, mapping only <see cref="WhatsAppChannelEndpoints"/>'s three routes) and a fake
/// WhatsApp host (a real, ephemeral-port Kestrel instance - <see cref="WhatsAppApiClientTests"/>/
/// <see cref="WhatsAppLiveTokenCheckTests"/>'s own established technique) that <see cref="WhatsAppApiClient"/>'s
/// own <see cref="HttpClient"/> is pointed at instead of the real <c>https://graph.facebook.com</c>.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class WhatsAppChannelStatusLiveCheckTests(OperatorOidcFixture fixture)
{
    private const string Token = "whatsapp-test-token-not-a-real-secret";
    private static readonly DateTimeOffset RegisteredAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    // A fixed, throwaway 32-byte AES-256 key (never a real secret - `ChannelCredentialCipherOptions`'s
    // own remarks) shared between `SeedActiveCredentialAsync`'s encrypt and `BuildTestHostAsync`'s own
    // `IChannelCredentialCipher` registration, so `HandleStatusAsync`'s live `cipher.Decrypt` call
    // recovers the exact plaintext token this file seeded - the identical need
    // `MaxChannelStatusLiveCheckTests`'/`VkChannelStatusLiveCheckTests`' own inline cipher construction
    // has for the identical reason.
    private static readonly ChannelCredentialCipher Cipher = new(
        new ChannelCredentialCipherOptions { CredentialEncryptionKey = Convert.ToBase64String(new byte[32]) });

    [Fact]
    public async Task GetWhatsAppStatus_WithAGoodToken_ReportsVerifiedAndBackfillsTheChangedDisplayNumber()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var phoneNumberId = FreshPhoneNumberId();
        var credential = await SeedActiveCredentialAsync(siteId, phoneNumberId, publicHandle: "+1 555-000-0000");

        await using var whatsAppHost = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{phoneNumberId}", () => Results.Json(
                new { id = phoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" })));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, whatsAppHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
        Assert.True(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Null(body.RefusalReason);

        // `25-176`'s own done-when: PublicHandle updates on a status read, not just at connect time.
        await using var db = fixture.CreateDbContext();
        var reloaded = await db.ChannelCredentials.FindAsync(credential.Id);
        Assert.Equal("+1 555-555-5555", reloaded!.PublicHandle);
    }

    [Fact]
    public async Task GetWhatsAppStatus_WithARevokedToken_ReportsVerifiedFalseWithAStatedReason()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var phoneNumberId = FreshPhoneNumberId();
        var credential = await SeedActiveCredentialAsync(siteId, phoneNumberId, publicHandle: null);

        await using var whatsAppHost = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{phoneNumberId}", () => Results.Json(
                new { error = new { message = "Invalid OAuth access token", type = "OAuthException", code = 190, error_subcode = (int?)null } },
                statusCode: 401)));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, whatsAppHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.False(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Contains("190", body.RefusalReason);
    }

    /// <summary>The three-way split this item exists to add: an unreachable WhatsApp (or this
    /// deployment's own egress to it) is a distinct fact from a refusal - a tenant should wait and retry,
    /// not go get a new token. The fake host here answers, but only after this test's own bound
    /// (`WhatsAppLiveTokenCheck.Timeout`, 5 seconds) would already have elapsed - the same "actually make
    /// it slow" standard <see cref="WhatsAppLiveTokenCheckTests"/>/<see cref="MaxChannelStatusLiveCheckTests"/>
    /// hold themselves to.</summary>
    [Fact]
    public async Task GetWhatsAppStatus_WhenWhatsAppIsUnreachable_ReportsUnreachable_DistinctFromARefusal()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var phoneNumberId = FreshPhoneNumberId();
        var credential = await SeedActiveCredentialAsync(siteId, phoneNumberId, publicHandle: null);

        await using var whatsAppHost = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{phoneNumberId}", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), requestAborted);
                return Results.Json(new { id = phoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, whatsAppHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Null(body.Verified);
        Assert.True(body.Unreachable);
        Assert.Null(body.RefusalReason);
    }

    [Fact]
    public async Task GetWhatsAppStatus_WithNoCredential_ReturnsNotConnected_AndNeverCallsWhatsApp()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var phoneNumberId = FreshPhoneNumberId();
        var whatsAppCalled = false;
        await using var whatsAppHost = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{phoneNumberId}", () =>
            {
                whatsAppCalled = true;
                return Results.Json(new { id = phoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, whatsAppHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WhatsAppChannelEndpoints.WhatsAppChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
        Assert.Null(body.Verified);
        Assert.False(body.Unreachable);
        Assert.False(whatsAppCalled);
    }

    // ------------------------------------------------------------------------------------------
    // Shared setup
    // ------------------------------------------------------------------------------------------

    private static string Route(SiteId siteId) => $"/api/v1/sites/{siteId.Value}/channels/whatsapp";

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

    /// <summary>A fresh WhatsApp phone number id per call, not a shared literal - the identical reason
    /// <see cref="VkChannelStatusLiveCheckTests"/>'s own <c>SeedActiveCredentialAsync</c> generates a
    /// fresh VK group id per call: <c>ux_channel_credentials_kind_provideraccountid_active</c> is a real
    /// production constraint (one active credential per WhatsApp phone number id, system-wide, not scoped
    /// per site) and this collection's own Postgres container is shared and unresettable across every
    /// test in this file.</summary>
    private static string FreshPhoneNumberId() => $"whatsapp-pn-{Guid.NewGuid():N}";

    private async Task<ChannelCredential> SeedActiveCredentialAsync(SiteId siteId, string phoneNumberId, string? publicHandle)
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.WhatsApp,
            tokenCiphertext: Cipher.Encrypt(Token), webhookSecretHash: [5, 6, 7, 8], RegisteredAt,
            providerAccountId: phoneNumberId, publicHandle: publicHandle);

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
        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.WhatsApp);
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

    private sealed record FakeWhatsAppHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port standing in for Meta's own Graph
    /// API - <see cref="WhatsAppApiClientTests"/>/<see cref="WhatsAppLiveTokenCheckTests"/>'s own
    /// established technique, reused here rather than shared (this project's own convention: every file
    /// that needs this shape builds its own small host).</summary>
    private static async Task<FakeWhatsAppHost> BuildFakeWhatsAppHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new FakeWhatsAppHost(app, baseUrl);
    }

    private async Task<WebApplication> BuildTestHostAsync(SiteId siteId, string whatsAppApiBaseUrl)
    {
        await GrantChannelEntitlementAsync(siteId);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // `ChannelEntitlementOptionKeys.For` + `ConfiguredBillingOptionEntitlementProvider`'s own
        // convention: the option key's own string doubles as the module key it grants - the identical
        // shorthand `ChannelStatusEndpointsTests`'/`MaxChannelStatusLiveCheckTests`'/
        // `VkChannelStatusLiveCheckTests`' own test hosts already use.
        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.WhatsApp);
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

        // `MapWhatsAppChannelEndpoints` maps `POST`/`DELETE` too - and, exactly as
        // `ChannelStatusEndpointsTests`'/`MaxChannelStatusLiveCheckTests`' own remarks found live, ASP.NET
        // Core's minimal-API routing infers every mapped delegate's own parameter sources for every
        // endpoint the moment `UseAuthorization()`'s `AuthorizationPolicyCache` first enumerates
        // `EndpointDataSource.Endpoints` at `app.StartAsync()` - so `HandleConnectAsync`/
        // `HandleDisconnectAsync`'s own services have to be resolvable here too, even though this file's
        // own tests only ever call `GET`.
        builder.Services.AddScoped<RegisterChannelCredentialHandler>();
        builder.Services.AddScoped<RevokeChannelCredentialHandler>();
        builder.Services.AddScoped<IWebhookSecretGenerator, WebhookSecretGenerator>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WhatsAppBotApiOptions()));
        builder.Services.AddScoped(_ => new WhatsAppApiClient(new HttpClient { BaseAddress = new Uri(whatsAppApiBaseUrl) }));

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

        app.MapWhatsAppChannelEndpoints();

        await app.StartAsync();
        return app;
    }
}
