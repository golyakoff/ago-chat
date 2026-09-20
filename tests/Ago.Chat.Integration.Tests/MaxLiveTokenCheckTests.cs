using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-174`: proves <see cref="MaxLiveTokenCheck"/>'s own bound actually bites, against a real slow HTTP
/// boundary rather than a code comment - the identical technique
/// <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveTokenCheckTests</c> already established for
/// Telegram's own equivalent, and the identical fake-host fixture <see cref="MaxApiClientTests"/> already
/// builds for <see cref="MaxApiClient.GetMeAsync"/> itself - reused here rather than shared, per this
/// project's own established pattern that every file needing this shape builds its own small host.
/// </summary>
public sealed class MaxLiveTokenCheckTests
{
    private const string Token = "max-test-token-not-a-real-secret";

    [Fact]
    public async Task RunAsync_WhenMaxAnswersWithinTheBound_ReturnsVerified()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { user_id = 1, is_bot = true, username = "shop_support_bot" })));
        var client = BuildClient(host.BaseUrl);

        var outcome = await MaxLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
        Assert.Equal("shop_support_bot", outcome.Username);
    }

    /// <summary>The other fact this outcome must never be confused with the timeout case below: MAX
    /// genuinely looked at the token and said no, within the bound, so this is a refusal - a tenant needs
    /// a new token, not a retry.</summary>
    [Fact]
    public async Task RunAsync_WhenMaxRejectsTheTokenWithinTheBound_ReturnsRefused_NotUnreachable()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", () => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)));
        var client = BuildClient(host.BaseUrl);

        var outcome = await MaxLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Contains("401", outcome.RefusalReason);
        Assert.Null(outcome.Username);
    }

    /// <summary>
    /// The proof the backlog item asked for: the provider call itself exceeds the bound (a real,
    /// deliberately slow response, not a simulated cancellation), and the result must be the unreachable
    /// case - never a refusal, and never <see cref="TimeoutException"/>/<see cref="OperationCanceledException"/>
    /// propagating uncaught.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenMaxTakesLongerThanTheBound_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", async (CancellationToken requestAborted) =>
            {
                // Deliberately longer than the bound this test passes below (200ms) - a real slow
                // response, the same "actually make it slow, not just claim it would be" standard
                // MaxApiClientTests/TelegramLiveTokenCheckTests hold themselves to.
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { user_id = 1, is_bot = true, username = "shop_support_bot" });
            }));
        var client = BuildClient(host.BaseUrl);

        var outcome = await MaxLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        // The sharpest part of the assertion: not merely "not verified", but "no refusal text either" -
        // RefusalReason staying null is what a caller (MaxChannelEndpoints.HandleStatusAsync) relies on
        // to render "could not reach MAX just now" rather than "MAX says this token is not valid".
        Assert.Null(outcome.RefusalReason);
        Assert.Null(outcome.Username);
    }

    /// <summary>The caller's own cancellation (a request the operator navigated away from, say) must
    /// still propagate as an ordinary <see cref="OperationCanceledException"/> rather than being
    /// swallowed into "unreachable" - only <em>this method's own</em> timeout is caught.</summary>
    [Fact]
    public async Task RunAsync_WhenTheCallersOwnTokenIsCancelled_PropagatesRatherThanReportingUnreachable()
    {
        await using var host = await BuildFakeMaxHostAsync(app =>
            app.MapGet("/me", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { user_id = 1, is_bot = true, username = "shop_support_bot" });
            }));
        var client = BuildClient(host.BaseUrl);
        using var callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // ThrowsAnyAsync, not ThrowsAsync: the concrete type here is TaskCanceledException (a subclass
        // HttpClient itself throws on a cancelled request) - a subtype of OperationCanceledException,
        // which is the fact this test actually cares about (it propagated rather than being caught and
        // turned into "unreachable").
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MaxLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), callerCts.Token));
    }

    private static MaxApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for MAX's own API -
    /// <see cref="MaxApiClientTests"/>'s own established technique in this project, reused here rather
    /// than shared as a fixture (this project's own convention: every file that needs this shape builds
    /// its own small host).</summary>
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
