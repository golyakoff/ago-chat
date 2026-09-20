using Ago.Chat.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-176`: proves <see cref="WhatsAppLiveTokenCheck"/>'s own bound actually bites, against a real slow
/// HTTP boundary rather than a code comment - the identical technique
/// <c>MaxLiveTokenCheckTests</c>/<c>VkLiveTokenCheckTests</c>/<c>TelegramLiveTokenCheckTests</c> already
/// established, reused here rather than shared, per this project's own established pattern that every
/// file needing this shape builds its own small host. The one WhatsApp-specific case none of those three
/// files has anything like - <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/> throwing
/// <see cref="WhatsAppApiCallException"/> rather than returning a result object's own <c>.Ok</c> flag, the
/// identical shape VK's own client has - is its own dedicated test below, and the backfilled-handle
/// assertion on the good-token test is the identical shape MAX's own test makes, unlike VK's.
/// </summary>
public sealed class WhatsAppLiveTokenCheckTests
{
    private const string Token = "whatsapp-test-token-not-a-real-secret";
    private const string PhoneNumberId = "106540352242922";

    [Fact]
    public async Task RunAsync_WhenWhatsAppAnswersWithinTheBound_ReturnsVerified()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", () => Results.Json(
                new { id = PhoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" })));
        var client = BuildClient(host.BaseUrl);

        var outcome = await WhatsAppLiveTokenCheck.RunAsync(client, Token, PhoneNumberId, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.False(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
        Assert.Equal("+1 555-555-5555", outcome.DisplayPhoneNumber);
    }

    /// <summary>The WhatsApp-specific branch neither Telegram's nor MAX's own check needs: WhatsApp's own
    /// client reports a terminal refusal by throwing <see cref="WhatsAppApiCallException"/>, not by
    /// returning a result object with an unset <c>.Ok</c> flag - the identical shape VK's own client has,
    /// and <see cref="WhatsAppLiveTokenCheck"/>'s own remarks explain why this file's control flow copies
    /// VK's, not MAX's.</summary>
    [Fact]
    public async Task RunAsync_WhenWhatsAppRejectsTheTokenWithinTheBound_ReturnsRefused_WithWhatsAppsOwnMessage_NotUnreachable()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", () => Results.Json(
                new { error = new { message = "Invalid OAuth access token", type = "OAuthException", code = 190, error_subcode = (int?)null } },
                statusCode: 401)));
        var client = BuildClient(host.BaseUrl);

        var outcome = await WhatsAppLiveTokenCheck.RunAsync(client, Token, PhoneNumberId, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Unreachable);
        // WhatsAppApiCallException's own message, verbatim - WhatsAppApiClient.GetPhoneNumberAsync's own
        // text includes the numeric error code.
        Assert.Contains("190", outcome.RefusalReason);
        Assert.Null(outcome.DisplayPhoneNumber);
    }

    /// <summary>
    /// The proof the backlog item asked for: the provider call itself exceeds the bound (a real,
    /// deliberately slow response, not a simulated cancellation), and the result must be the unreachable
    /// case - never a refusal, and never <see cref="TimeoutException"/>/<see cref="OperationCanceledException"/>
    /// propagating uncaught.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenWhatsAppTakesLongerThanTheBound_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", async (CancellationToken requestAborted) =>
            {
                // Deliberately longer than the bound this test passes below (200ms) - a real slow
                // response, the same "actually make it slow, not just claim it would be" standard
                // MaxLiveTokenCheckTests/VkLiveTokenCheckTests/TelegramLiveTokenCheckTests hold themselves to.
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { id = PhoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" });
            }));
        var client = BuildClient(host.BaseUrl);

        var outcome = await WhatsAppLiveTokenCheck.RunAsync(client, Token, PhoneNumberId, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        // The sharpest part of the assertion: not merely "not verified", but "no refusal text either" -
        // RefusalReason staying null is what a caller (WhatsAppChannelEndpoints.HandleStatusAsync) relies
        // on to render "could not reach WhatsApp just now" rather than "WhatsApp says this token is not
        // valid".
        Assert.Null(outcome.RefusalReason);
        Assert.Null(outcome.DisplayPhoneNumber);
    }

    /// <summary>The caller's own cancellation (a request the operator navigated away from, say) must
    /// still propagate as an ordinary <see cref="OperationCanceledException"/> rather than being
    /// swallowed into "unreachable" - only <em>this method's own</em> timeout is caught.</summary>
    [Fact]
    public async Task RunAsync_WhenTheCallersOwnTokenIsCancelled_PropagatesRatherThanReportingUnreachable()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", async (CancellationToken requestAborted) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), requestAborted);
                return Results.Json(new { id = PhoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" });
            }));
        var client = BuildClient(host.BaseUrl);
        using var callerCts = new CancellationTokenSource();
        callerCts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // ThrowsAnyAsync, not ThrowsAsync: the concrete type here is TaskCanceledException (a subclass
        // HttpClient itself throws on a cancelled request) - a subtype of OperationCanceledException,
        // which is the fact this test actually cares about (it propagated rather than being caught and
        // turned into "unreachable").
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WhatsAppLiveTokenCheck.RunAsync(client, Token, PhoneNumberId, TimeSpan.FromSeconds(5), callerCts.Token));
    }

    /// <summary>An unreachable host (a real closed socket, not WhatsApp answering) must also report
    /// <c>Unreachable</c>, exactly as a timeout does - <see cref="WhatsAppLiveTokenCheck"/>'s own
    /// <c>catch (HttpRequestException)</c> branch, the identical case
    /// <c>WhatsAppApiClientTests.SendMessageAsync_WhenWhatsAppIsUnreachable_ThrowsARealConnectionFailure</c>
    /// proves for <see cref="WhatsAppApiClient"/> itself.</summary>
    [Fact]
    public async Task RunAsync_WhenWhatsAppIsUnreachable_ReturnsUnreachable_NotRefused()
    {
        await using var host = await BuildFakeWhatsAppHostAsync(app =>
            app.MapGet($"/{PhoneNumberId}", () => Results.Json(
                new { id = PhoneNumberId, display_phone_number = "+1 555-555-5555", verified_name = "Test Shop" })));
        var client = BuildClient(host.BaseUrl);
        await host.App.StopAsync();

        var outcome = await WhatsAppLiveTokenCheck.RunAsync(client, Token, PhoneNumberId, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Unreachable);
        Assert.Null(outcome.RefusalReason);
    }

    private static WhatsAppApiClient BuildClient(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for Meta's own Graph
    /// API - <see cref="MaxLiveTokenCheckTests"/>'s/<see cref="WhatsAppApiClientTests"/>'s own established
    /// technique in this project, reused here rather than shared as a fixture (this project's own
    /// convention: every file that needs this shape builds its own small host).</summary>
    private static async Task<TestHost> BuildFakeWhatsAppHostAsync(Action<WebApplication> configureRoutes)
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
