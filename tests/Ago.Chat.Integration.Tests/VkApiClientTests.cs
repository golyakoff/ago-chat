using Ago.Chat.Infrastructure.Vk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-08`: <see cref="VkApiClient"/>'s own terminal/transient split, proven against a real HTTP
/// boundary rather than trusting a code comment - <see cref="TelegramApiClientTests"/>'s own precedent
/// for scaling <see cref="MaxChannelAdapterResilienceTests"/>'s technique down to an in-process,
/// ephemeral-port Kestrel host rather than a whole separate fake-process project: the breaker/timeout/
/// bulkhead mechanism itself is already proven generically (<c>ResilientInboundChannelAdapterTests</c>)
/// and concretely against a real provider (<see cref="MaxChannelAdapterResilienceTests"/>), so what this
/// item's own report needs proven is VK's real error shape - specifically, the one thing neither MAX nor
/// Telegram needed a test for at all: that <see cref="VkApiClient"/> reads the JSON body's own
/// <c>error</c>/<c>response</c> keys rather than the HTTP status code, because VK answers both success
/// and failure with HTTP 200 (<see cref="VkApiClient"/>'s own remarks).
/// </summary>
public sealed class VkApiClientTests
{
    private const string Token = "test-community-token-not-a-real-secret";
    private const string ApiVersion = "5.199";

    [Fact]
    public async Task SendMessageAsync_WhenVkAnswersWithAResponse_ReturnsSentWithTheProviderMessageId()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/messages.send", () => Results.Json(new { response = 555 })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, groupId: 1, peerId: 42, "hello", randomId: 7, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("555", result.ProviderMessageId);
    }

    /// <summary>901 ("the user has not allowed messages from this community") is one of VK's own
    /// messages-specific refusal codes (confirmed from VK's own SDK's <c>ExceptionMapper.php</c> -
    /// <see cref="VkApiClient"/>'s own remarks) - a permanent condition for this message, never worth
    /// retrying, so it must come back as a refusal rather than an exception. The response is a genuine
    /// HTTP 200 throughout - VK's own convention, unlike MAX's/Telegram's HTTP status codes.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenVkRefusesWithATerminalErrorCode_ReturnsRefusedRatherThanThrowing()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/messages.send", () => Results.Json(
                new { error = new { error_code = 901, error_msg = "Can't send messages for users without permission" } })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.SendMessageAsync(Token, groupId: 1, peerId: 42, "hello", randomId: 7, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("901", result.RefusalReason);
    }

    /// <summary>9 (flood control) is deliberately excluded from the terminal list even though VK answers
    /// it with the identical HTTP 200 shape as a terminal refusal - excluding it is what lets the
    /// wrapping resilience pipeline's own retry/backoff actually help, rather than every flood-limited
    /// send being silently given up on (<see cref="VkApiClient"/>'s own remarks on the default-to-transient
    /// reasoning).</summary>
    [Fact]
    public async Task SendMessageAsync_WhenVkReturnsAFloodControlErrorCode_ThrowsRatherThanRefusing()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/messages.send", () => Results.Json(new { error = new { error_code = 9, error_msg = "Flood control" } })));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, groupId: 1, peerId: 42, "hello", randomId: 7, CancellationToken.None));
    }

    /// <summary>The literal "provider unreachable" case, against a real closed socket - the host is
    /// started, its base URL captured, then stopped before the client ever calls it, the same "actually
    /// stop it" standard <see cref="MaxChannelAdapterResilienceTests"/>/<see cref="TelegramApiClientTests"/>
    /// hold themselves to.</summary>
    [Fact]
    public async Task SendMessageAsync_WhenVkIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/messages.send", () => Results.Json(new { response = 1 })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendMessageAsync(Token, groupId: 1, peerId: 42, "hello", randomId: 7, CancellationToken.None));
    }

    [Fact]
    public async Task GetGroupInfoAsync_WhenVkAnswersWithAGroup_ReturnsItsId()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } })));

        var client = BuildClient(host.BaseUrl);

        var info = await client.GetGroupInfoAsync(Token, CancellationToken.None);

        Assert.Equal(987654L, info.GroupId);
        Assert.Equal("Test Shop", info.Name);
    }

    [Fact]
    public async Task GetGroupInfoAsync_WhenVkRejectsTheToken_ThrowsVkApiCallException()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { error = new { error_code = 5, error_msg = "User authorization failed" } })));

        var client = BuildClient(host.BaseUrl);

        var ex = await Assert.ThrowsAsync<VkApiCallException>(() => client.GetGroupInfoAsync(Token, CancellationToken.None));
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public async Task GetCallbackConfirmationCodeAsync_WhenVkAnswers_ReturnsTheCode()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getCallbackConfirmationCode", () => Results.Json(new { response = new { code = "a1b2c3d4" } })));

        var client = BuildClient(host.BaseUrl);

        var code = await client.GetCallbackConfirmationCodeAsync(Token, groupId: 1, CancellationToken.None);

        Assert.Equal("a1b2c3d4", code);
    }

    private static VkApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) }, ApiVersion);

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for VK's own API -
    /// <see cref="TelegramApiClientTests"/>'s own established technique in this project, reused here for
    /// a second channel provider's HTTP boundary.</summary>
    private static async Task<TestHost> BuildFakeVkHostAsync(Action<WebApplication> configureRoutes)
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
