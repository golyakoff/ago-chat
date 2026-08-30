using Ago.Chat.Infrastructure.Avito;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-11`: <see cref="AvitoApiClient"/>'s own terminal/transient split, proven against a real HTTP
/// boundary - <see cref="VkApiClientTests"/>'s own established technique in this project, reused here for
/// a fifth channel provider's HTTP boundary. What this item's own report needs proven is Avito's real
/// shape: a genuine non-200 status carries the outcome (unlike VK's always-200 convention), and a 401
/// specifically means something this provider-specific (an expired 24-hour access token, not a simply-bad
/// credential) - <see cref="AvitoAccessTokenExpiredException"/>'s own remarks.
/// </summary>
public sealed class AvitoApiClientTests
{
    private const string AccessToken = "test-avito-access-token-not-a-real-secret";

    [Fact]
    public async Task GetSelfAsync_WhenAvitoAnswers_ReturnsTheAccountId()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", () => Results.Json(new { id = 94235311, name = "Test Seller" })));

        var client = BuildClient(host.BaseUrl);

        var self = await client.GetSelfAsync(AccessToken, CancellationToken.None);

        Assert.Equal(94235311, self.Id);
    }

    [Fact]
    public async Task GetSelfAsync_WhenAvitoRejectsTheToken_ThrowsAvitoApiCallException()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapGet("/core/v1/accounts/self", () => Results.Json(
                new { error = new { code = 401, message = "Unauthorized" } }, statusCode: 401)));

        var client = BuildClient(host.BaseUrl);

        var ex = await Assert.ThrowsAsync<AvitoApiCallException>(() => client.GetSelfAsync(AccessToken, CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task SubscribeWebhookAsync_WhenAvitoAnswersOk_CompletesWithoutThrowing()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v3/webhook", () => Results.Json(new { ok = true })));

        var client = BuildClient(host.BaseUrl);

        await client.SubscribeWebhookAsync(AccessToken, new Uri("https://ago.example/webhooks/avito/abc?secret=s"), CancellationToken.None);
        // No exception - the entire contract of this call.
    }

    [Fact]
    public async Task SubscribeWebhookAsync_WhenAvitoRejects_ThrowsAvitoApiCallException()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v3/webhook", () => Results.Json(
                new { error = new { code = 403, message = "Forbidden" } }, statusCode: 403)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<AvitoApiCallException>(() =>
            client.SubscribeWebhookAsync(AccessToken, new Uri("https://ago.example/webhooks/avito/abc?secret=s"), CancellationToken.None));
    }

    [Fact]
    public async Task SendMessageAsync_WhenAvitoAnswersWithASentMessage_ReturnsSentWithTheProviderMessageId()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages",
                () => Results.Json(new { id = "msg-555", direction = "out", type = "text", created = 123 })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(AccessToken, 94235311, "chat-1", "hello", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("msg-555", result.ProviderMessageId);
    }

    /// <summary>404 ("the chat or account does not exist") is one of this item's own terminal refusal
    /// statuses (<see cref="AvitoApiClient"/>'s own remarks) - a permanent condition for this message,
    /// never worth retrying.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenAvitoRefusesWithATerminalStatus_ReturnsRefusedRatherThanThrowing()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", () => Results.Json(
                new { error = new { code = 404, message = "Chat not found" } }, statusCode: 404)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(AccessToken, 94235311, "chat-1", "hello", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("404", result.RefusalReason);
    }

    /// <summary>The one status this item's own terminal set deliberately excludes -
    /// <see cref="AvitoAccessTokenExpiredException"/>'s own remarks on why a 401 here means something
    /// routine (a 24-hour access token that aged out), not a bad credential.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenAvitoAnswersUnauthorized_ThrowsAvitoAccessTokenExpiredException()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", () => Results.Json(
                new { error = new { code = 401, message = "token expired" } }, statusCode: 401)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<AvitoAccessTokenExpiredException>(
            () => client.SendMessageAsync(AccessToken, 94235311, "chat-1", "hello", CancellationToken.None));
    }

    /// <summary>429 (rate limiting) is deliberately excluded from the terminal list - the same
    /// "err toward retrying" default every precedent in this stage applies to an unclassified code.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenAvitoRateLimits_ThrowsRatherThanRefusing()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", () => Results.StatusCode(429)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(AccessToken, 94235311, "chat-1", "hello", CancellationToken.None));
    }

    /// <summary>The literal "provider unreachable" case, against a real closed socket -
    /// <see cref="VkApiClientTests"/>'s own established technique for this project.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenAvitoIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/messenger/v1/accounts/{userId}/chats/{chatId}/messages", () => Results.Json(new { id = "msg-1" })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(AccessToken, 94235311, "chat-1", "hello", CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_WhenAvitoAnswers_ReturnsTheRotatedPair()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/token", () => Results.Json(new
            {
                access_token = "new-access-token",
                refresh_token = "new-refresh-token",
                expires_in = 86400,
                token_type = "Bearer",
                scope = "messenger:read,messenger:write",
            })));

        var client = BuildClient(host.BaseUrl);

        var refreshed = await client.RefreshAccessTokenAsync("client-id", "client-secret", "old-refresh-token", CancellationToken.None);

        Assert.Equal("new-access-token", refreshed.AccessToken);
        Assert.Equal("new-refresh-token", refreshed.RefreshToken);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_WhenAvitoRejectsTheRefreshToken_ThrowsAvitoApiCallException()
    {
        await using var host = await BuildFakeAvitoHostAsync(app =>
            app.MapPost("/token", () => Results.Json(
                new { error = new { code = 400, message = "invalid_grant" } }, statusCode: 400)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<AvitoApiCallException>(
            () => client.RefreshAccessTokenAsync("client-id", "client-secret", "old-refresh-token", CancellationToken.None));
    }

    private static AvitoApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Avito's own API -
    /// <see cref="VkApiClientTests"/>'s own established technique, reused here for a fifth provider's HTTP
    /// boundary.</summary>
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
}
