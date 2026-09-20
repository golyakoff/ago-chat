using Ago.Chat.Infrastructure.Vk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-175`: proves <see cref="VkLiveTokenCheck"/>'s own bound actually bites, against a real slow HTTP
/// boundary rather than a code comment - the identical technique
/// <c>Ago.Chat.Integration.Tests.MaxLiveTokenCheckTests</c>/<c>TelegramLiveTokenCheckTests</c> already
/// established, reused here rather than shared, per this project's own established pattern that every
/// file needing this shape builds its own small host. The one VK-specific case those two files have
/// nothing like - <see cref="VkApiClient.GetGroupInfoAsync"/> throwing <see cref="VkApiCallException"/>
/// rather than returning a result object's own <c>.Ok</c> flag - is its own dedicated test below.
/// </summary>
public sealed class VkLiveTokenCheckTests
{
    private const string Token = "vk-test-community-token-not-a-real-secret";
    private const string ApiVersion = "5.199";

    [Fact]
    public async Task RunAsync_WhenVkAnswersWithinTheBound_ReturnsVerified()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { response = new { groups = new[] { new { id = 987654L, name = "Test Shop" } } } })));
        var client = BuildClient(host.BaseUrl);

        var outcome = await VkLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    /// <summary>The VK-specific branch neither Telegram's nor MAX's own check needs: VK's own client
    /// reports a terminal refusal by throwing <see cref="VkApiCallException"/>, not by returning a result
    /// object with an unset <c>.Ok</c> flag - <see cref="VkLiveTokenCheck"/>'s own remarks on why this is
    /// the one place its shape genuinely differs from either sibling.</summary>
    [Fact]
    public async Task RunAsync_WhenVkRejectsTheTokenWithinTheBound_ReturnsRefused_WithVksOwnMessage_NotUnreachable()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { error = new { error_code = 5, error_msg = "User authorization failed" } })));
        var client = BuildClient(host.BaseUrl);

        var outcome = await VkLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        // VkApiCallException's own message, verbatim - VkApiClient.GetGroupInfoAsync's own text includes
        // both the numeric error code and VK's own error_msg.
        Assert.Contains("5", outcome.RefusalReason);
        Assert.Contains("User authorization failed", outcome.RefusalReason);
    }

    /// <summary>
    /// The proof the backlog item asked for: the provider call itself exceeds the bound (a real,
    /// deliberately slow response, not a simulated cancellation), and the result must be the unreachable
    /// case - never a refusal, and never <see cref="TimeoutException"/>/<see cref="OperationCanceledException"/>
    /// propagating uncaught.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenVkTakesLongerThanTheBound_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", async (CancellationToken requestAborted) =>
            {
                // Deliberately longer than the bound this test passes below (200ms) - a real slow
                // response, the same "actually make it slow, not just claim it would be" standard
                // MaxLiveTokenCheckTests/TelegramLiveTokenCheckTests hold themselves to.
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { response = new { groups = new[] { new { id = 1L, name = "Test Shop" } } } });
            }));
        var client = BuildClient(host.BaseUrl);

        var outcome = await VkLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        // The sharpest part of the assertion: not merely "not verified", but "no refusal text either" -
        // RefusalReason staying null is what a caller (VkChannelEndpoints.HandleStatusAsync) relies on to
        // render "could not reach VK just now" rather than "VK says this token is not valid".
        Assert.Null(outcome.RefusalReason);
    }

    /// <summary>The caller's own cancellation (a request the operator navigated away from, say) must
    /// still propagate as an ordinary <see cref="OperationCanceledException"/> rather than being
    /// swallowed into "unreachable" - only <em>this method's own</em> timeout is caught.</summary>
    [Fact]
    public async Task RunAsync_WhenTheCallersOwnTokenIsCancelled_PropagatesRatherThanReportingUnreachable()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { response = new { groups = new[] { new { id = 1L, name = "Test Shop" } } } });
            }));
        var client = BuildClient(host.BaseUrl);
        using var callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // ThrowsAnyAsync, not ThrowsAsync: the concrete type here is TaskCanceledException (a subclass
        // HttpClient itself throws on a cancelled request) - a subtype of OperationCanceledException,
        // which is the fact this test actually cares about (it propagated rather than being caught and
        // turned into "unreachable").
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => VkLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), callerCts.Token));
    }

    /// <summary>An unreachable host (a real closed socket, not VK answering) must also report
    /// <c>Unreachable</c>, exactly as a timeout does - <see cref="VkLiveTokenCheck"/>'s own
    /// <c>catch (HttpRequestException)</c> branch, the identical case
    /// <c>VkApiClientTests.SendMessageAsync_WhenVkIsUnreachable_ThrowsARealConnectionFailure</c> proves
    /// for <see cref="VkApiClient"/> itself.</summary>
    [Fact]
    public async Task RunAsync_WhenVkIsUnreachable_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeVkHostAsync(app =>
            app.MapPost("/groups.getById", () => Results.Json(
                new { response = new { groups = new[] { new { id = 1L, name = "Test Shop" } } } })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        var outcome = await VkLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    private static VkApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) }, ApiVersion);

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for VK's own API -
    /// <see cref="MaxLiveTokenCheckTests"/>'s/<see cref="VkApiClientTests"/>'s own established technique
    /// in this project, reused here rather than shared as a fixture (this project's own convention: every
    /// file that needs this shape builds its own small host).</summary>
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
