using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-177`: proves <see cref="AvitoLiveTokenCheck"/>'s own bound and its eager-refresh-on-expiry behaviour
/// against a real slow/failing HTTP boundary rather than a code comment - the identical technique
/// <c>TelegramLiveTokenCheckTests</c>/<c>MaxLiveTokenCheckTests</c>/<c>VkLiveTokenCheckTests</c>/
/// <c>WhatsAppLiveTokenCheckTests</c> already established. The one case none of those four files has
/// anything like - a refresh, persisted, then re-verified with a second live call - gets its own tests
/// below; <see cref="AvitoChannelStatusLiveCheckTests"/> is where the concurrent-refresh race itself is
/// proven, against a real Postgres row and two real concurrent HTTP requests, because that race is a
/// database-visibility question this file's own single-threaded, fake-repository tests cannot honestly
/// exercise (<see cref="AvitoLiveTokenCheck"/>'s own remarks on why the reload is retried at all explain
/// the reasoning that test needs to prove).
/// </summary>
public sealed class AvitoLiveTokenCheckTests
{
    private const string ClientId = "avito-test-client-id";
    private const string ClientSecret = "avito-test-client-secret-not-a-real-secret";

    private static readonly DateTimeOffset RegisteredAt = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AvitoApiOptions Options = new() { ClientId = ClientId, ClientSecret = ClientSecret };

    [Fact]
    public async Task RunAsync_WithAGoodUnexpiredToken_ReturnsVerified_AndNeverAttemptsARefresh()
    {
        var refreshAttempted = false;
        await using var host = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", () => Results.Json(new { id = 94235311 }));
            app.MapPost("/token", () =>
            {
                refreshAttempted = true;
                return Results.StatusCode(500);
            });
        });
        var client = BuildClient(host.BaseUrl);
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "good-access-token", refreshToken: "unused-refresh-token");
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
        Assert.False(refreshAttempted);
    }

    /// <summary>The Decision's own central case: an expired-but-refreshable token refreshes immediately,
    /// persists the new pair, and reports <c>Verified: true</c> for the *new* token - the caller never
    /// sees the transient "was expired a moment ago" fact.</summary>
    [Fact]
    public async Task RunAsync_WithAnExpiredButRefreshableToken_RefreshesPersistsAndReturnsVerified()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", async (HttpContext ctx) =>
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
                    ? Results.Json(new
                    {
                        access_token = "new-access-token",
                        refresh_token = "new-refresh-token",
                        expires_in = 86400,
                        token_type = "Bearer",
                    })
                    : Results.Json(new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400);
            });
        });
        var client = BuildClient(host.BaseUrl);
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "old-access-token", refreshToken: "old-refresh-token");
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
        Assert.Equal("new-access-token", cipher.Decrypt(credential.TokenCiphertext));
        Assert.Equal("new-refresh-token", cipher.Decrypt(credential.RefreshTokenCiphertext!));
        Assert.True(repository.SaveCalled);
    }

    /// <summary>A genuinely revoked grant: the initial check fails, and the reactive refresh fails too
    /// (Avito rejects the stored refresh token outright, not because anyone else raced it - the reload
    /// below finds the very same refresh token still sitting in "the database", so
    /// <see cref="AvitoLiveTokenCheck"/> cannot read this as someone else's win).</summary>
    [Fact]
    public async Task RunAsync_WithAGenuinelyRevokedGrant_RefreshAlsoFails_ReturnsRefused()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", () => Results.Json(
                new { error = new { code = 401, message = "token expired" } }, statusCode: 401));
            app.MapPost("/token", () => Results.Json(
                new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400));
        });
        var client = BuildClient(host.BaseUrl);
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "dead-access-token", refreshToken: "dead-refresh-token");
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.NotNull(outcome.RefusalReason);
        Assert.False(repository.SaveCalled);
    }

    /// <summary>No refresh token stored at all - <see cref="AvitoChannelAdapter.SendAsync"/>'s own "no
    /// refresh token stored" branch, mirrored here: nothing to attempt, so this reports the original
    /// failure directly rather than ever calling <c>/token</c>.</summary>
    [Fact]
    public async Task RunAsync_WithNoRefreshTokenStored_ReturnsRefused_WithoutCallingTokenEndpoint()
    {
        var tokenEndpointCalled = false;
        await using var host = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapGet("/core/v1/accounts/self", () => Results.Json(
                new { error = new { code = 401, message = "token expired" } }, statusCode: 401));
            app.MapPost("/token", () =>
            {
                tokenEndpointCalled = true;
                return Results.StatusCode(500);
            });
        });
        var client = BuildClient(host.BaseUrl);
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "bad-access-token", refreshToken: null);
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Contains("401", outcome.RefusalReason);
        Assert.False(tokenEndpointCalled);
    }

    [Fact]
    public async Task RunAsync_WhenAvitoTakesLongerThanTheBound_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { id = 94235311 });
            }));
        var client = BuildClient(host.BaseUrl);
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "some-access-token", refreshToken: "some-refresh-token");
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    [Fact]
    public async Task RunAsync_WhenAvitoIsUnreachable_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", () => Results.Json(new { id = 94235311 })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();
        var repository = new FakeChannelCredentialRepository();
        var cipher = new PassthroughCipher();
        var credential = SeedCredential(cipher, accessToken: "some-access-token", refreshToken: "some-refresh-token");
        repository.Seed(credential);

        var outcome = await AvitoLiveTokenCheck.RunAsync(
            client, repository, cipher, Options, credential, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    private static ChannelCredential SeedCredential(PassthroughCipher cipher, string accessToken, string? refreshToken) =>
        ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), ChannelKind.Avito,
            tokenCiphertext: cipher.Encrypt(accessToken), webhookSecretHash: [1, 2, 3], RegisteredAt,
            providerAccountId: "94235311",
            refreshTokenCiphertext: refreshToken is null ? null : cipher.Encrypt(refreshToken));

    private static AvitoApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Avito's own API -
    /// <see cref="AvitoApiClientTests"/>'s own established technique, reused here for this check's own
    /// HTTP boundary.</summary>
    private static async Task<TestHost> BuildFakeAvitoHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseUrl = addresses.First() + "/";

        return new TestHost(app, baseUrl);
    }

    /// <summary>A trivial, non-encrypting stand-in - this file's own tests care about which plaintext
    /// value ends up where, not about AES-GCM itself (already proven by
    /// <c>ChannelCredentialCipherTests</c>).</summary>
    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Decrypt(byte[] ciphertext) => System.Text.Encoding.UTF8.GetString(ciphertext);
    }

    /// <summary>Holds exactly one credential, by reference - this file's own tests never race two
    /// concurrent callers against this fake (that proof lives in <see cref="AvitoChannelStatusLiveCheckTests"/>
    /// instead, against a real Postgres row), so a reload has nothing to disagree with: the instance
    /// <see cref="AvitoLiveTokenCheck"/> already holds *is* "the database" here.</summary>
    private sealed class FakeChannelCredentialRepository : IChannelCredentialRepository
    {
        private ChannelCredential? _credential;

        public bool SaveCalled { get; private set; }

        public void Seed(ChannelCredential credential) => _credential = credential;

        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(_credential);

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            Task.FromResult(_credential is not null && _credential.Id == id ? _credential : null);

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
            ChannelKind kind, string providerAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken)
        {
            SaveCalled = true;
            _credential = credential;
            return Task.CompletedTask;
        }

        public Task ReloadAsync(ChannelCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
