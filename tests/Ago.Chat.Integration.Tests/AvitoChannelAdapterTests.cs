using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-11`: <see cref="AvitoChannelAdapter.SendAsync"/>'s own routing/resolution logic - the parts
/// specific to this adapter rather than the generic resilience wrapping around it
/// (<see cref="ResilientInboundChannelAdapterTests"/>'s own scope) or Avito's real HTTP error shape
/// (<see cref="AvitoApiClientTests"/>'s own scope). Uses the same minimal fake-repository technique
/// <see cref="VkChannelAdapterTests"/> already establishes, extended with a stateful
/// <see cref="FakeChannelCredentialRepository"/> - unlike VK's fixed one, this item's own reactive
/// token-refresh path genuinely reloads and mutates the credential mid-<c>SendAsync</c>
/// (<see cref="AvitoChannelAdapter"/>'s own remarks), so the fake needs to behave like storage, not just
/// answer one fixed value.
/// </summary>
public sealed class AvitoChannelAdapterTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static OutboundChannelMessage Reply(Guid messageId, string chatId = "chat-1") => new(
        ChannelKind.Avito, new ExternalChannelAddress(chatId), ConversationId, new MessageId(messageId),
        new MessageBody("an operator's answer"));

    [Fact]
    public async Task SendAsync_WhenAvitoAnswers_ReturnsSentWithTheProviderMessageId()
    {
        await using var fakeAvito = await BuildFakeAvitoHostAsync(SendRespondsWith(() => Results.Json(new { id = "msg-555" })));
        var adapter = BuildAdapter(fakeAvito.BaseUrl, providerAccountId: "94235311");

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal("msg-555", outcome.ProviderMessageId);
    }

    [Fact]
    public async Task SendAsync_WhenNoActiveCredentialExists_ReturnsRefused()
    {
        await using var fakeAvito = await BuildFakeAvitoHostAsync(SendRespondsWith(() => Results.Json(new { id = "msg-1" })));
        var adapter = BuildAdapter(fakeAvito.BaseUrl, providerAccountId: "94235311", hasActiveCredential: false);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("No active Avito account", outcome.FailureReason);
    }

    [Fact]
    public async Task SendAsync_WhenTheCredentialHasNoProviderAccountId_Throws()
    {
        await using var fakeAvito = await BuildFakeAvitoHostAsync(SendRespondsWith(() => Results.Json(new { id = "msg-1" })));
        var adapter = BuildAdapter(fakeAvito.BaseUrl, providerAccountId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None));
    }

    /// <summary>This item's own central new mechanism - <see cref="AvitoChannelAdapter"/>'s own remarks
    /// on why a 401 triggers exactly one refresh-and-retry, and why both rotated secrets must be
    /// persisted (Avito rotates the refresh token on every use) before the retry.</summary>
    [Fact]
    public async Task SendAsync_WhenAvitoRefusesWithAnExpiredToken_RefreshesAndRetriesOnce()
    {
        var attempt = 0;
        await using var fakeAvito = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", (HttpContext ctx) =>
            {
                attempt++;
                return attempt == 1
                    ? Results.Json(new { error = new { code = 401, message = "expired" } }, statusCode: 401)
                    : Results.Json(new { id = "msg-after-refresh" });
            });
            app.MapPost("/token", () => Results.Json(new
            {
                access_token = "refreshed-access-token",
                refresh_token = "refreshed-refresh-token",
                expires_in = 86400,
            }));
        });
        var credentials = new FakeChannelCredentialRepository(hasActiveCredential: true, providerAccountId: "94235311", refreshTokenCiphertext: "old-refresh-token");
        var adapter = BuildAdapter(fakeAvito.BaseUrl, credentials);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal("msg-after-refresh", outcome.ProviderMessageId);
        Assert.Equal(2, attempt);
        // Both rotated secrets landed in storage, not just used in-memory for the retry.
        Assert.Equal("refreshed-access-token", credentials.LastSavedAccessToken);
        Assert.Equal("refreshed-refresh-token", credentials.LastSavedRefreshToken);
    }

    [Fact]
    public async Task SendAsync_WhenTheTokenExpiresAndNoRefreshTokenIsStored_ReturnsRefused()
    {
        await using var fakeAvito = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages",
                () => Results.Json(new { error = new { code = 401, message = "expired" } }, statusCode: 401)));
        var credentials = new FakeChannelCredentialRepository(hasActiveCredential: true, providerAccountId: "94235311", refreshTokenCiphertext: null);
        var adapter = BuildAdapter(fakeAvito.BaseUrl, credentials);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("no refresh token is stored", outcome.FailureReason);
    }

    [Fact]
    public async Task SendAsync_WhenAvitoRejectsTheRefreshAttempt_ReturnsRefused()
    {
        await using var fakeAvito = await BuildFakeAvitoHostAsync(app =>
        {
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages",
                () => Results.Json(new { error = new { code = 401, message = "expired" } }, statusCode: 401));
            app.MapPost("/token", () => Results.Json(new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400));
        });
        var credentials = new FakeChannelCredentialRepository(hasActiveCredential: true, providerAccountId: "94235311", refreshTokenCiphertext: "old-refresh-token");
        var adapter = BuildAdapter(fakeAvito.BaseUrl, credentials);

        var outcome = await adapter.SendAsync(Reply(Guid.NewGuid()), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("refused to refresh", outcome.FailureReason);
    }

    private static Action<WebApplication> SendRespondsWith(Func<IResult> respond) =>
        app => app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", respond);

    private static AvitoChannelAdapter BuildAdapter(string avitoBaseUrl, string? providerAccountId, bool hasActiveCredential = true) =>
        BuildAdapter(avitoBaseUrl, new FakeChannelCredentialRepository(hasActiveCredential, providerAccountId, refreshTokenCiphertext: null));

    private static AvitoChannelAdapter BuildAdapter(string avitoBaseUrl, FakeChannelCredentialRepository credentials)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => credentials);
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = new Uri(avitoBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var apiClient = new AvitoApiClient(httpClient);
        var options = Options.Create(new AvitoApiOptions { ClientId = "ago-client-id", ClientSecret = "ago-client-secret" });

        return new AvitoChannelAdapter(apiClient, options, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AvitoChannelAdapter>.Instance);
    }

    private sealed record FakeAvitoHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<FakeAvitoHost> BuildFakeAvitoHostAsync(Action<WebApplication> configureRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        configureRoutes(app);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new FakeAvitoHost(app, addresses.First() + "/");
    }

    private sealed class FixedConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow));

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Unlike VK's own fixed fake, this one is genuinely stateful - <see cref="GetByIdAsync"/>
    /// and <see cref="SaveAsync"/> round-trip a real in-memory <see cref="ChannelCredential"/>, because
    /// <see cref="AvitoChannelAdapter"/>'s own reactive-refresh path reloads and mutates the credential
    /// mid-call (this class's own remarks).</summary>
    private sealed class FakeChannelCredentialRepository(bool hasActiveCredential, string? providerAccountId, string? refreshTokenCiphertext) : IChannelCredentialRepository
    {
        private ChannelCredential? _credential = hasActiveCredential
            ? ChannelCredential.Register(
                new ChannelCredentialId(Guid.NewGuid()), SiteId, ChannelKind.Avito, PassthroughCipher.EncryptStatic("initial-access-token"),
                [4, 5, 6], DateTimeOffset.UtcNow, providerAccountId,
                refreshTokenCiphertext is null ? null : PassthroughCipher.EncryptStatic(refreshTokenCiphertext))
            : null;

        public string? LastSavedAccessToken { get; private set; }

        public string? LastSavedRefreshToken { get; private set; }

        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(_credential);

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            Task.FromResult(_credential is not null && _credential.Id == id ? _credential : null);

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken)
        {
            _credential = credential;
            LastSavedAccessToken = PassthroughCipher.DecryptStatic(credential.TokenCiphertext);
            LastSavedRefreshToken = credential.RefreshTokenCiphertext is { } bytes ? PassthroughCipher.DecryptStatic(bytes) : null;
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => EncryptStatic(token);

        public string Decrypt(byte[] ciphertext) => DecryptStatic(ciphertext);

        public static byte[] EncryptStatic(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public static string DecryptStatic(byte[] ciphertext) => System.Text.Encoding.UTF8.GetString(ciphertext);
    }
}
