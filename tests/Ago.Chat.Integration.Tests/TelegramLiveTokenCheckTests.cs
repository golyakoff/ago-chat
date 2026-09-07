using Ago.Chat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-36`: proves <see cref="TelegramLiveTokenCheck"/>'s own bound actually bites, against a real
/// slow HTTP boundary rather than a code comment - <see cref="TelegramApiClientTests"/>'s own
/// real-Kestrel-host technique, reused here rather than duplicated as a shared fixture (this project's
/// own established pattern: every file that needs this shape builds its own small host, per
/// <see cref="TelegramApiClientTests"/>, <see cref="VkApiClientTests"/> and the rest).
///
/// <para>Found while landing `23-36`: the console's live status read (<c>Ago.Chat.Api.Channels.
/// TelegramChannelEndpoints.HandleStatusAsync</c>) called <c>TelegramApiClient.GetMeAsync</c> with no
/// bound at all, inheriting <c>HttpClient</c>'s own 100-second default - tolerable for the connect-time
/// call it was copied from (a deliberate one-off action), not for a read that runs on every page load,
/// especially given this deployment's own SOCKS5 relay to Telegram (`TelegramProxyOptions`) is itself a
/// second thing that can be down. This file is what proves the fix rather than trusting the doc
/// comment: a deliberately slow fake Telegram, a deliberately short bound, and an assertion that the
/// result is the <em>unreachable</em> case and never the <em>invalid-token</em> one - the two facts a
/// tenant would otherwise confuse into acting on the wrong advice (retry, versus get a new token).</para>
/// </summary>
public sealed class TelegramLiveTokenCheckTests
{
    private const string Token = "123456:test-token-not-a-real-secret";

    [Fact]
    public async Task RunAsync_WhenTelegramAnswersWithinTheBound_ReturnsVerified()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () => Results.Json(new { ok = true, result = new { id = 1, is_bot = true } })));
        var client = BuildClient(host.BaseUrl);

        var outcome = await TelegramLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    /// <summary>The other fact this outcome must never be confused with the timeout case below:
    /// Telegram genuinely looked at the token and said no, within the bound, so this is a refusal - a
    /// tenant needs a new token, not a retry.</summary>
    [Fact]
    public async Task RunAsync_WhenTelegramRejectsTheTokenWithinTheBound_ReturnsRefused_NotUnreachable()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", () =>
                Results.Json(
                    new { ok = false, error_code = 401, description = "Unauthorized" },
                    statusCode: StatusCodes.Status401Unauthorized)));
        var client = BuildClient(host.BaseUrl);

        var outcome = await TelegramLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Contains("401", outcome.RefusalReason);
    }

    /// <summary>
    /// The proof the coordinator asked for: the provider call itself exceeds the bound (a real,
    /// deliberately slow response, not a simulated cancellation), and the result must be the
    /// unreachable case - never a refusal, and never <see cref="TimeoutException"/>/
    /// <see cref="OperationCanceledException"/> propagating uncaught, which is exactly what an
    /// unbound call did before this item.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTelegramTakesLongerThanTheBound_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", async (CancellationToken requestAborted) =>
            {
                // Deliberately longer than the bound this test passes below (200ms) - a real slow
                // response, the same "actually make it slow, not just claim it would be" standard
                // TelegramApiClientTests holds itself to for "actually stop it".
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { ok = true, result = new { id = 1, is_bot = true } });
            }));
        var client = BuildClient(host.BaseUrl);

        var outcome = await TelegramLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        // The sharpest part of the assertion: not merely "not verified", but "no refusal text either" -
        // RefusalReason staying null is what a caller (TelegramChannelEndpoints.HandleStatusAsync) relies
        // on to render "could not reach Telegram just now" rather than "Telegram says this token is not
        // valid". A regression that collapsed the two cases would still pass Assert.False(outcome.Ok)
        // above; this line is what actually distinguishes them.
        Assert.Null(outcome.RefusalReason);
    }

    /// <summary>The caller's own cancellation (a request the operator navigated away from, say) must
    /// still propagate as an ordinary <see cref="OperationCanceledException"/> rather than being
    /// swallowed into "unreachable" - only <em>this method's own</em> timeout is caught.</summary>
    [Fact]
    public async Task RunAsync_WhenTheCallersOwnTokenIsCancelled_PropagatesRatherThanReportingUnreachable()
    {
        await using var host = await BuildFakeTelegramHostAsync(app =>
            app.MapGet($"/bot{Token}/getMe", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { ok = true, result = new { id = 1, is_bot = true } });
            }));
        var client = BuildClient(host.BaseUrl);
        using var callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // ThrowsAnyAsync, not ThrowsAsync: the concrete type here is TaskCanceledException (a
        // subclass HttpClient itself throws on a cancelled request) - a subtype of
        // OperationCanceledException, which is the fact this test actually cares about (it propagated
        // rather than being caught and turned into "unreachable").
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TelegramLiveTokenCheck.RunAsync(client, Token, TimeSpan.FromSeconds(5), callerCts.Token));
    }

    private static TelegramApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Telegram's own
    /// API - <see cref="TelegramApiClientTests"/>'s own established technique in this project, reused
    /// here rather than shared as a fixture (this project's own convention: every file that needs this
    /// shape builds its own small host).</summary>
    private static async Task<TestHost> BuildFakeTelegramHostAsync(Action<WebApplication> configureRoutes)
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
