using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-147`: <see cref="MaxApiClient.GetMeAsync"/>'s own terminal/transient split, proven against a real
/// HTTP boundary the identical lightweight way <see cref="TelegramApiClientTests"/> already established
/// for Telegram's own <c>getMe</c> - a real (in-process, ephemeral-port) Kestrel host, not the separate
/// <c>Ago.Chat.FakeMax</c> process <see cref="MaxChannelAdapterResilienceTests"/> stands up for the
/// resilience-pipeline proof that call already has. This one new method needs only a real HTTP boundary
/// that can answer different status codes, which the smaller fixture gives for a fraction of the
/// ceremony - <see cref="TelegramApiClientTests"/>'s own remarks give the fuller version of this same
/// reasoning.
/// </summary>
public sealed class MaxApiClientTests
{
    private const string Token = "max-test-token-not-a-real-secret";

    [Fact]
    public async Task GetMeAsync_WhenMaxAnswersOk_ReturnsTheBotsOwnUsername()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { user_id = 1, is_bot = true, username = "shop_support_bot" })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("shop_support_bot", result.Username);
    }

    [Fact]
    public async Task GetMeAsync_WhenMaxAnswersOkWithNoUsername_ReturnsNullUsername()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { user_id = 1, is_bot = true })));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Username);
    }

    /// <summary>401 is this item's own reasoned default for "MAX refused the token" - a terminal
    /// refusal, never worth retrying, so it must come back as a result the caller inspects rather than an
    /// exception - the same shape <see cref="MaxApiClient.SendMessageAsync"/> already gives for the
    /// identical status code.</summary>
    [Fact]
    public async Task GetMeAsync_WhenMaxRejectsWith401_ReturnsRefused()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)));

        var client = BuildClient(host.BaseUrl);

        var result = await client.GetMeAsync(Token, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Username);
        Assert.Contains("401", result.RefusalReason);
    }

    /// <summary>A server-shaped error is transient, never a refusal - the identical
    /// "client-shaped errors are refusals, server-shaped errors are transient" default this client's own
    /// remarks state for <see cref="MaxApiClient.SendMessageAsync"/>.</summary>
    [Fact]
    public async Task GetMeAsync_WhenMaxReturns500_Throws()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));

        var client = BuildClient(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMeAsync(Token, CancellationToken.None));
    }

    private static MaxApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<TestHost> BuildFakeMaxHostAsync(Action<WebApplication> configureRoutes)
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
