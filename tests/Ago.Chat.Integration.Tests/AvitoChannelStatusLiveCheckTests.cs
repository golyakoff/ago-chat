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
/// `25-177`: real HTTP proof that <c>AvitoChannelEndpoints.HandleStatusAsync</c> now re-verifies live on
/// every read, and eagerly refreshes an expired-but-refreshable token rather than only reporting it -
/// the same parity `25-174`/`25-175`/`25-176` gave MAX/VK/WhatsApp
/// (<see cref="WhatsAppChannelStatusLiveCheckTests"/>, this file's own direct template) - both files exist
/// because <see cref="ChannelStatusEndpointsTests"/>'s own remarks now explicitly scope Avito's
/// "connected" case out of that shared file too: Avito's route no longer fits "whose routes never call
/// any provider" the moment this item ships.
///
/// <para>Two real HTTP boundaries are stood up per test: the operator-facing API host (a real
/// <c>TestServer</c>, mapping only <see cref="AvitoChannelEndpoints"/>'s three routes) and a fake Avito
/// host (a real, ephemeral-port Kestrel instance - <see cref="AvitoApiClientTests"/>/
/// <see cref="AvitoLiveTokenCheckTests"/>'s own established technique) that <see cref="AvitoApiClient"/>'s
/// own <see cref="HttpClient"/> is pointed at instead of the real <c>https://api.avito.ru</c>.</para>
///
/// <para><b>The concurrency race is proven here, not in <see cref="AvitoLiveTokenCheckTests"/>.</b> The
/// Decision's own risk (`docs/backlog/25-177-*.md`) is a race between "Avito already knows who won the
/// one-shot refresh token" and "this deployment's own database reflects the winner's write" - a genuine
/// database-visibility question <see cref="AvitoLiveTokenCheckTests"/>'s own single-threaded fake
/// repository cannot honestly stand in for. <see cref="GetAvitoStatus_TwoConcurrentReadsAgainstTheSameExpiredToken_BothSucceed"/>
/// below fires two real, concurrent HTTP requests at one running host, backed by this collection's own
/// real Postgres container, and a fake Avito host that enforces genuine one-shot refresh-token semantics
/// under a lock - so whichever of the two requests reaches <c>/token</c> first is a real race, not a
/// scripted one, and <see cref="AvitoLiveTokenCheck"/>'s own private reload-on-race loop (through the real
/// EF-backed <see cref="ChannelCredentialRepository.ReloadAsync"/>, not a fake) is what this test actually
/// exercises.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class AvitoChannelStatusLiveCheckTests(OperatorOidcFixture fixture)
{
    private static readonly DateTimeOffset RegisteredAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    // A fixed, throwaway 32-byte AES-256 key (never a real secret - `ChannelCredentialCipherOptions`'s
    // own remarks) shared between `SeedActiveCredentialAsync`'s encrypt and `BuildTestHostAsync`'s own
    // `IChannelCredentialCipher` registration - `WhatsAppChannelStatusLiveCheckTests`'s own identical need.
    private static readonly ChannelCredentialCipher Cipher = new(
        new ChannelCredentialCipherOptions { CredentialEncryptionKey = Convert.ToBase64String(new byte[32]) });

    [Fact]
    public async Task GetAvitoStatus_WithAGoodUnexpiredToken_ReportsVerified_AndNeverTouchesPublicHandle()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var providerAccountId = FreshProviderAccountId();
        var credential = await SeedActiveCredentialAsync(siteId, providerAccountId, accessToken: "good-access-token", refreshToken: "some-refresh-token");

        await using var avitoHost = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", () => Results.Json(new { id = 94235311 })));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Equal(RegisteredAt, body.CreatedAt);
        Assert.True(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Null(body.RefusalReason);

        // `25-177`'s own explicit non-goal, per `25-147`: this live check never writes PublicHandle.
        await using var db = fixture.CreateDbContext();
        var reloaded = await db.ChannelCredentials.FindAsync(credential.Id);
        Assert.Null(reloaded!.PublicHandle);
    }

    [Fact]
    public async Task GetAvitoStatus_WithAGenuinelyRevokedGrant_ReportsVerifiedFalseWithAStatedReason()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var providerAccountId = FreshProviderAccountId();
        var credential = await SeedActiveCredentialAsync(siteId, providerAccountId, accessToken: "dead-access-token", refreshToken: "dead-refresh-token");

        await using var avitoHost = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", () => Results.Json(
                new { error = new { code = 401, message = "token expired" } }, statusCode: 401));
            app.MapPost("/token", () => Results.Json(
                new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400));
        });

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.False(body.Verified);
        Assert.False(body.Unreachable);
        Assert.NotNull(body.RefusalReason);
    }

    [Fact]
    public async Task GetAvitoStatus_WithAnExpiredButRefreshableToken_RefreshesAndReportsVerified_ForTheNewToken()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var providerAccountId = FreshProviderAccountId();
        var credential = await SeedActiveCredentialAsync(siteId, providerAccountId, accessToken: "old-access-token", refreshToken: "old-refresh-token");

        await using var avitoHost = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", (HttpContext ctx) =>
            {
                var presented = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
                return presented == "new-access-token"
                    ? Results.Json(new { id = 94235311 })
                    : Results.Json(new { error = new { code = 401, message = "token expired" } }, statusCode: 401);
            });
            app.MapPost("/token", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                return form["refresh_token"] == "old-refresh-token"
                    ? Results.Json(new { access_token = "new-access-token", refresh_token = "new-refresh-token", expires_in = 86400 })
                    : Results.Json(new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400);
            });
        });

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        // The Decision's own point: the caller never sees the transient "was expired a moment ago" fact.
        Assert.True(body.Verified);
        Assert.False(body.Unreachable);
        Assert.Null(body.RefusalReason);

        await using var db = fixture.CreateDbContext();
        var reloaded = await db.ChannelCredentials.FindAsync(credential.Id);
        Assert.Equal("new-access-token", Cipher.Decrypt(reloaded!.TokenCiphertext));
        Assert.Equal("new-refresh-token", Cipher.Decrypt(reloaded.RefreshTokenCiphertext!));
    }

    /// <summary>The three-way split this item exists to add: an unreachable Avito (or this deployment's
    /// own egress to it) is a distinct fact from a refusal - a tenant should wait and retry, not go
    /// reconnect the channel. The fake host here answers, but only after this test's own bound
    /// (<c>AvitoLiveTokenCheck.Timeout</c>, 5 seconds) would already have elapsed - the same "actually make
    /// it slow" standard <see cref="AvitoLiveTokenCheckTests"/>/<see cref="WhatsAppChannelStatusLiveCheckTests"/>
    /// hold themselves to.</summary>
    [Fact]
    public async Task GetAvitoStatus_WhenAvitoIsUnreachable_ReportsUnreachable_DistinctFromARefusal()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var providerAccountId = FreshProviderAccountId();
        var credential = await SeedActiveCredentialAsync(siteId, providerAccountId, accessToken: "some-access-token", refreshToken: "some-refresh-token");

        await using var avitoHost = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), requestAborted);
                return Results.Json(new { id = 94235311 });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Connected);
        Assert.Equal(credential.Id.Value, body.ChannelCredentialId);
        Assert.Null(body.Verified);
        Assert.True(body.Unreachable);
        Assert.Null(body.RefusalReason);
    }

    [Fact]
    public async Task GetAvitoStatus_WithNoCredential_ReturnsNotConnected_AndNeverCallsAvito()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);

        var avitoCalled = false;
        await using var avitoHost = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", () =>
            {
                avitoCalled = true;
                return Results.Json(new { id = 94235311 });
            }));

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var client = CreateClient(host, token);

        var response = await client.GetAsync(Route(siteId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Connected);
        Assert.Null(body.ChannelCredentialId);
        Assert.False(avitoCalled);
    }

    /// <summary>
    /// `25-177`'s own required Done-when item: two concurrent status reads against the same expired
    /// access token both succeed - one wins the real race to refresh Avito's one-shot refresh token, the
    /// other's own attempt is rejected by the fake Avito host (a real lock enforcing "first presented
    /// value wins", not a scripted ordering) and recovers by reloading the real Postgres row through
    /// <see cref="ChannelCredentialRepository.ReloadAsync"/> - proving
    /// <see cref="AvitoLiveTokenCheck"/>'s own private reload-on-race loop actually bridges the gap
    /// between "Avito already knows who won" and "the winner's own database write has landed", not merely
    /// that the code compiles.
    /// </summary>
    [Fact]
    public async Task GetAvitoStatus_TwoConcurrentReadsAgainstTheSameExpiredToken_BothSucceed()
    {
        var siteId = await CreateFreshSiteAsync();
        await GrantChannelManageAsync(siteId);
        var providerAccountId = FreshProviderAccountId();
        var credential = await SeedActiveCredentialAsync(siteId, providerAccountId, accessToken: "expired-access-token", refreshToken: "shared-refresh-token");

        await using var avitoHost = await BuildRacingFakeAvitoHostAsync(initialRefreshToken: "shared-refresh-token");

        var token = await fixture.GetDemoAdminAccessTokenAsync();
        await using var host = await BuildTestHostAsync(siteId, avitoHost.BaseUrl);
        using var clientA = CreateClient(host, token);
        using var clientB = CreateClient(host, token);

        var responseA = clientA.GetAsync(Route(siteId));
        var responseB = clientB.GetAsync(Route(siteId));
        await Task.WhenAll(responseA, responseB);

        var bodyA = await (await responseA).Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();
        var bodyB = await (await responseB).Content.ReadFromJsonAsync<AvitoChannelEndpoints.AvitoChannelStatusResponse>();

        Assert.Equal(HttpStatusCode.OK, (await responseA).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await responseB).StatusCode);

        foreach (var body in new[] { bodyA, bodyB })
        {
            Assert.NotNull(body);
            Assert.True(body!.Connected);
            Assert.True(body.Verified, $"Expected Verified: true, got Verified={body.Verified}, Unreachable={body.Unreachable}, RefusalReason={body.RefusalReason}");
            Assert.False(body.Unreachable);
            Assert.Null(body.RefusalReason);
        }

        // Exactly one refresh actually won at Avito's own side, and the database ends up holding that
        // one winning pair - not corrupted by the loser's own failed attempt.
        await using var db = fixture.CreateDbContext();
        var reloaded = await db.ChannelCredentials.FindAsync(credential.Id);
        Assert.Equal("rotated-access-token-1", Cipher.Decrypt(reloaded!.TokenCiphertext));
        Assert.Equal("rotated-refresh-token-1", Cipher.Decrypt(reloaded.RefreshTokenCiphertext!));
    }

    // ------------------------------------------------------------------------------------------
    // Shared setup
    // ------------------------------------------------------------------------------------------

    private static string Route(SiteId siteId) => $"/api/v1/sites/{siteId.Value}/channels/avito";

    private async Task<SiteId> CreateFreshSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    /// <summary>A fresh Avito provider account id per call, not a shared literal -
    /// `WhatsAppChannelStatusLiveCheckTests.FreshPhoneNumberId`'s own reasoning: this collection's own
    /// Postgres container is shared and unresettable across every test in this file.</summary>
    private static string FreshProviderAccountId() => Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<ChannelCredential> SeedActiveCredentialAsync(
        SiteId siteId, string providerAccountId, string accessToken, string refreshToken)
    {
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Avito,
            tokenCiphertext: Cipher.Encrypt(accessToken), webhookSecretHash: [5, 6, 7, 8], RegisteredAt,
            providerAccountId: providerAccountId, refreshTokenCiphertext: Cipher.Encrypt(refreshToken));

        await using var db = fixture.CreateDbContext();
        db.ChannelCredentials.Add(credential);
        await db.SaveChangesAsync();

        return credential;
    }

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
        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.Avito);
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

    private sealed record FakeAvitoHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port standing in for Avito's own API -
    /// <see cref="AvitoApiClientTests"/>/<see cref="AvitoLiveTokenCheckTests"/>'s own established
    /// technique, reused here (this project's own convention: every file that needs this shape builds its
    /// own small host).</summary>
    private static async Task<FakeAvitoHost> BuildFakeAvitoHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new FakeAvitoHost(app, baseUrl);
    }

    /// <summary>
    /// The one fake host in this file that actually enforces Avito's own one-shot, rotating refresh-token
    /// contract under a real lock, rather than a single scripted answer - what
    /// <see cref="GetAvitoStatus_TwoConcurrentReadsAgainstTheSameExpiredToken_BothSucceed"/> needs to make
    /// the race genuine rather than assumed. <c>GET /core/v1/accounts/self</c> accepts only whatever access
    /// token is "currently valid"; <c>POST /token</c> accepts only whatever refresh token is "currently
    /// valid", rotates both under the lock, and rejects a second, now-stale presenter with the identical
    /// generic <c>invalid_grant</c> envelope <see cref="AvitoApiClientTests"/> already proves is the real
    /// shape of every <c>/token</c> failure - so the losing request genuinely cannot tell "stale" from
    /// "revoked" from Avito's own answer alone, exactly as <see cref="AvitoLiveTokenCheck"/>'s own remarks
    /// describe.
    /// </summary>
    private static async Task<FakeAvitoHost> BuildRacingFakeAvitoHostAsync(string initialRefreshToken)
    {
        var gate = new object();
        // Deliberately never equal to any access token this test ever presents - the whole point of this
        // fixture is that the *seeded* access token is already expired, so GetSelfAsync must 401 on it
        // from the very first call, forcing both concurrent readers through the refresh path rather than
        // one of them getting lucky and short-circuiting on a "still valid" initial token.
        var currentAccessToken = "(no access token has ever been issued by this fake host)";
        var currentRefreshToken = initialRefreshToken;
        var rotationCount = 0;

        return await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", (HttpContext ctx) =>
            {
                var presented = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
                bool ok;
                lock (gate)
                {
                    ok = presented == currentAccessToken;
                }

                return ok
                    ? Results.Json(new { id = 94235311 })
                    : Results.Json(new { error = new { code = 401, message = "token expired" } }, statusCode: 401);
            });

            app.MapPost("/token", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                var presentedRefreshToken = form["refresh_token"].ToString();

                string? newAccessToken = null;
                string? newRefreshToken = null;
                lock (gate)
                {
                    if (presentedRefreshToken == currentRefreshToken)
                    {
                        rotationCount++;
                        newAccessToken = $"rotated-access-token-{rotationCount}";
                        newRefreshToken = $"rotated-refresh-token-{rotationCount}";
                        currentAccessToken = newAccessToken;
                        currentRefreshToken = newRefreshToken;
                    }
                }

                if (newAccessToken is null)
                {
                    // The identical generic envelope AvitoApiClientTests proves every real /token failure
                    // uses - this fake deliberately gives the loser no distinguishing signal at all.
                    return Results.Json(new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400);
                }

                return Results.Json(new { access_token = newAccessToken, refresh_token = newRefreshToken, expires_in = 86400 });
            });
        });
    }

    private async Task<WebApplication> BuildTestHostAsync(SiteId siteId, string avitoApiBaseUrl)
    {
        await GrantChannelEntitlementAsync(siteId);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var optionKey = ChannelEntitlementOptionKeys.For(ChannelKind.Avito);
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

        // `MapAvitoChannelEndpoints` maps `POST`/`DELETE` too - and, exactly as
        // `ChannelStatusEndpointsTests`'/`WhatsAppChannelStatusLiveCheckTests`' own remarks found live,
        // ASP.NET Core's minimal-API routing infers every mapped delegate's own parameter sources for
        // every endpoint the moment `UseAuthorization()`'s `AuthorizationPolicyCache` first enumerates
        // `EndpointDataSource.Endpoints` at `app.StartAsync()` - so `HandleConnectAsync`/
        // `HandleDisconnectAsync`'s own services have to be resolvable here too, even though this file's
        // own tests only ever call `GET`.
        builder.Services.AddScoped<Application.UseCases.RegisterChannelCredential.RegisterChannelCredentialHandler>();
        builder.Services.AddScoped<Application.UseCases.RevokeChannelCredential.RevokeChannelCredentialHandler>();
        builder.Services.AddScoped<IWebhookSecretGenerator, WebhookSecretGenerator>();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AvitoApiOptions
        {
            ClientId = "avito-test-client-id",
            ClientSecret = "avito-test-client-secret-not-a-real-secret",
        }));
        builder.Services.AddScoped(_ => new AvitoApiClient(new HttpClient { BaseAddress = new Uri(avitoApiBaseUrl) }));

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

        app.MapAvitoChannelEndpoints();

        await app.StartAsync();
        return app;
    }
}
