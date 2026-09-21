using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.RuStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-04`: <see cref="RuStorePushSender"/>'s own terminal/transient split, proven against a real HTTP
/// boundary rather than trusting a code comment - <see cref="VkApiClientTests"/>'s own precedent for an
/// in-process, ephemeral-port Kestrel host standing in for the real provider, reused here for a seventh
/// outbound integration and RuStore Push's genuinely different outcome shape: every one of its five
/// documented codes is a real, parseable JSON body (`26-04`'s own backlog item found this live against a
/// garbage bearer token), so all five are values here, never thrown exceptions - unlike every channel
/// adapter this codebase already has, where only some codes are.
///
/// <para><b>What this class does and does not prove.</b> It proves exactly what this item's own report
/// must say plainly: what JSON this adapter sends for a given <see cref="PushMessage"/>
/// (<see cref="SendAsync_SendsTheDataOnlyRequestThisItemsOwnBestGuessReadingProduces"/>), and how it
/// classifies every one of RuStore's five documented outcomes plus the live-observed sixth. It proves
/// nothing about whether RuStore's real service actually accepts this exact body - there is no live
/// RuStore project or service token in this deployment to send a real message against, and no test here
/// claims otherwise.</para>
/// </summary>
public sealed class RuStorePushSenderTests
{
    private const string ProjectId = "test-project-id";
    private const string ServiceToken = "test-service-token-not-a-real-secret";

    [Fact]
    public async Task SendAsync_WhenRuStoreAnswersSuccessfully_ReturnsDelivered()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(new { })));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.Delivered>(outcome);
    }

    /// <summary>
    /// Pins down exactly what JSON body this adapter sends - the test this item's own report points to
    /// for the `message.data.payload` ambiguity (`adr/0180` §3): a flat `data` map with
    /// `title`/`body`/`groupKey` plus whatever `PushMessage.Data` itself carries, no `payload` wrapper
    /// key, no `notification` object, no `priority` or `collapse_key` (RuStore's schema has neither -
    /// `adr/0180` §4b). <see cref="RuStorePushSender.BuildData"/>'s own remarks state this is this
    /// item's best-guess reading, not a verified one.
    /// </summary>
    [Fact]
    public async Task SendAsync_SendsTheDataOnlyRequestThisItemsOwnBestGuessReadingProduces()
    {
        string? capturedBody = null;
        string? capturedAuthHeader = null;
        string? capturedPath = null;
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", async (HttpContext ctx) =>
            {
                capturedPath = ctx.Request.Path.Value;
                capturedAuthHeader = ctx.Request.Headers.Authorization.ToString();
                using var reader = new StreamReader(ctx.Request.Body);
                capturedBody = await reader.ReadToEndAsync();
                return Results.Json(new { });
            }));

        var sender = BuildSender(host.BaseUrl);
        var message = new PushMessage(
            DeviceToken: "device-token-abc",
            Title: "New message",
            Body: "Visitor a1b2c3d4 is waiting",
            GroupKey: "ago-conversation-11111111-1111-1111-1111-111111111111",
            TimeToLive: TimeSpan.FromMinutes(5),
            Data: new Dictionary<string, string>
            {
                ["conversationId"] = "11111111-1111-1111-1111-111111111111",
                ["messageId"] = "22222222-2222-2222-2222-222222222222",
            });

        await sender.SendAsync(message, CancellationToken.None);

        Assert.Equal($"/v1/projects/{ProjectId}/messages:send", capturedPath);
        Assert.Equal($"Bearer {ServiceToken}", capturedAuthHeader);

        var expectedJson = """
            {
              "message": {
                "token": "device-token-abc",
                "data": {
                  "conversationId": "11111111-1111-1111-1111-111111111111",
                  "messageId": "22222222-2222-2222-2222-222222222222",
                  "title": "New message",
                  "body": "Visitor a1b2c3d4 is waiting",
                  "groupKey": "ago-conversation-11111111-1111-1111-1111-111111111111"
                },
                "android": {
                  "ttl": "300s"
                }
              }
            }
            """;

        // JsonNode.DeepEquals treats a JSON object as an unordered set of properties (unlike a plain
        // string comparison, which would make this test depend on System.Text.Json's own property
        // ordering) - the identical property-count-and-value assertion, done structurally rather than
        // one property at a time.
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(capturedBody!)),
            $"Expected:\n{expectedJson}\n\nActual:\n{capturedBody}");
    }

    [Fact]
    public async Task SendAsync_WhenRuStoreReturns400InvalidArgument_ReturnsTokenGone()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 400, message = "The registration token is not a valid FCM registration token", status = "INVALID_ARGUMENT" },
                statusCode: 400)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var tokenGone = Assert.IsType<PushSendOutcome.TokenGone>(outcome);
        Assert.Contains("INVALID_ARGUMENT", tokenGone.Reason);
    }

    [Fact]
    public async Task SendAsync_WhenRuStoreReturns404NotFound_ReturnsTokenGone()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 404, message = "Requested entity was not found.", status = "NOT_FOUND" },
                statusCode: 404)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TokenGone>(outcome);
    }

    /// <summary>The case this item's own backlog is most insistent about: a bad *service* key must
    /// never revoke a device row. `403 PERMISSION_DENIED` is RuStore's own documented code for it.</summary>
    [Fact]
    public async Task SendAsync_WhenRuStoreReturns403PermissionDenied_ReturnsTransientFailure_NeverTokenGone()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 403, message = "The caller does not have permission.", status = "PERMISSION_DENIED" },
                statusCode: 403)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var transient = Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
        Assert.Contains("PERMISSION_DENIED", transient.Reason);
    }

    /// <summary>The live-observed sixth outcome (`26-04`'s own Done-when): a malformed bearer token drew
    /// a real `401 UNAUTHORIZED` body, not RuStore's documented `403`. Both must be treated identically -
    /// our own credential's fault, never a device fault.</summary>
    [Fact]
    public async Task SendAsync_WhenRuStoreReturns401Unauthorized_ReturnsTransientFailure_NeverTokenGone()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 401, message = "unauthorized: Invalid S2S token", status = "UNAUTHORIZED" },
                statusCode: 401)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
    }

    [Fact]
    public async Task SendAsync_WhenRuStoreReturns429TooManyRequests_ReturnsTransientFailure()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 429, message = "Resource has been exhausted.", status = "TOO_MANY_REQUESTS" },
                statusCode: 429)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
    }

    [Fact]
    public async Task SendAsync_WhenRuStoreReturns500Internal_ReturnsTransientFailure()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 500, message = "Internal server error.", status = "INTERNAL" },
                statusCode: 500)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
    }

    /// <summary>`adr/0180` §9's own honest gap: named in the `status` field's example values, absent
    /// from the enumerated error list - treated as terminal if it ever arrives, without depending on
    /// it.</summary>
    [Fact]
    public async Task SendAsync_WhenRuStoreReturnsUnregistered_ReturnsTokenGone()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { code = 404, message = "The registration token is no longer valid.", status = "UNREGISTERED" },
                statusCode: 404)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TokenGone>(outcome);
    }

    /// <summary>The literal "provider unreachable" case, against a real closed socket - the same
    /// "actually stop it" standard <see cref="WhatsAppApiClientTests"/>/<see cref="VkApiClientTests"/>
    /// hold themselves to.</summary>
    [Fact]
    public async Task SendAsync_WhenRuStoreIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(new { })));
        var sender = BuildSender(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(BuildMessage(), CancellationToken.None));
    }

    /// <summary>A non-200 response with no parseable RuStore envelope at all - an edge/CDN rejection,
    /// not a documented outcome. Thrown, not classified, so the wrapping resilience pipeline's retry
    /// gets a chance at a real answer instead of this adapter guessing one.</summary>
    [Fact]
    public async Task SendAsync_WhenTheResponseHasNoParseableRuStoreBody_Throws()
    {
        await using var host = await BuildFakeRuStoreHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Text("<html>502 Bad Gateway</html>", statusCode: 502)));

        var sender = BuildSender(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(BuildMessage(), CancellationToken.None));
    }

    private static PushMessage BuildMessage() => new(
        DeviceToken: "device-token-abc",
        Title: "New message",
        Body: "Visitor a1b2c3d4 is waiting",
        GroupKey: "ago-conversation-11111111-1111-1111-1111-111111111111",
        TimeToLive: TimeSpan.FromMinutes(5),
        Data: new Dictionary<string, string> { ["conversationId"] = "11111111-1111-1111-1111-111111111111" });

    // Mirrors Ago.Chat.Worker/Program.cs' own composition-root setup for RuStorePushSender's typed
    // HttpClient - the Authorization header is set once there, never inside RuStorePushSender itself
    // (this class' own remarks), so a test double has to reproduce it explicitly rather than getting it
    // for free from the production DI wiring.
    private static RuStorePushSender BuildSender(string baseUrl) => new(new HttpClient
    {
        BaseAddress = new Uri($"{baseUrl}v1/projects/{ProjectId}/"),
        DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken) },
    });

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for RuStore's own
    /// send API - <see cref="VkApiClientTests"/>'s own established technique in this project, reused
    /// here for a seventh outbound provider's HTTP boundary.</summary>
    private static async Task<TestHost> BuildFakeRuStoreHostAsync(Action<WebApplication> configureRoutes)
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
