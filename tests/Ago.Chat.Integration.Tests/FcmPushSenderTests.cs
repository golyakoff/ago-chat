using System.Text.Json.Nodes;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Fcm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-100`/`adr/0181`: <see cref="FcmPushSender"/>'s own terminal/transient split and data-only body,
/// proven against a real HTTP boundary - the identical in-process, ephemeral-port Kestrel technique
/// <see cref="RuStorePushSenderTests"/> uses for RuStore, reused here for FCM's own error shape. The
/// OAuth2 access token is supplied by a stub token provider, so this test needs no service account, no
/// private key and no call to Google - the seam <see cref="IFcmAccessTokenProvider"/> exists for.
///
/// <para>Proves what JSON this adapter sends (data-only, high priority, no notification block, the same
/// keys RuStore sends) and how it classifies FCM's outcomes; it proves nothing about whether Google's real
/// service accepts this exact body, the same honest limit <see cref="RuStorePushSenderTests"/> states.</para>
/// </summary>
public sealed class FcmPushSenderTests
{
    private const string ProjectId = "ago-chat-783f7";
    private const string AccessToken = "ya29.stub-access-token-not-a-real-secret";

    [Fact]
    public async Task SendAsync_WhenFcmAnswersSuccessfully_ReturnsDelivered()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(new { name = "projects/ago-chat-783f7/messages/1" })));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.Delivered>(outcome);
    }

    [Fact]
    public async Task SendAsync_SendsADataOnlyHighPriorityRequestWithTheSameKeysAsRuStore()
    {
        string? capturedBody = null;
        string? capturedAuthHeader = null;
        string? capturedPath = null;
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", async (HttpContext ctx) =>
            {
                capturedPath = ctx.Request.Path.Value;
                capturedAuthHeader = ctx.Request.Headers.Authorization.ToString();
                using var reader = new StreamReader(ctx.Request.Body);
                capturedBody = await reader.ReadToEndAsync();
                return Results.Json(new { name = "projects/ago-chat-783f7/messages/1" });
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
        Assert.Equal($"Bearer {AccessToken}", capturedAuthHeader);

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
                  "ttl": "300s",
                  "priority": "high"
                }
              }
            }
            """;

        // JsonNode.DeepEquals compares as an unordered set of properties, independent of serializer
        // ordering - the same structural assertion RuStorePushSenderTests uses. Its success also proves
        // the negative the data-only design turns on: there is no "notification" property anywhere.
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(capturedBody!)),
            $"Expected:\n{expectedJson}\n\nActual:\n{capturedBody}");
    }

    [Fact]
    public async Task SendAsync_WhenFcmReturnsUnregistered_ReturnsTokenGone()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                FcmErrorBody(404, "Requested entity was not found.", "NOT_FOUND", "UNREGISTERED"),
                statusCode: 404)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var tokenGone = Assert.IsType<PushSendOutcome.TokenGone>(outcome);
        Assert.Contains("UNREGISTERED", tokenGone.Reason);
    }

    [Fact]
    public async Task SendAsync_WhenFcmReturnsNotFoundWithNoDetailCode_ReturnsTokenGone()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { error = new { code = 404, message = "Requested entity was not found.", status = "NOT_FOUND" } },
                statusCode: 404)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TokenGone>(outcome);
    }

    /// <summary>The deliberate narrowing versus RuStore (`FcmPushSender`'s own remarks): a `400
    /// INVALID_ARGUMENT` can equally be a malformed request of ours, and for FCM token-death is signalled
    /// specifically by `UNREGISTERED`, so this is transient - never a revocation - erring toward keeping
    /// the row.</summary>
    [Fact]
    public async Task SendAsync_WhenFcmReturns400InvalidArgument_ReturnsTransientFailure_NeverTokenGone()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                FcmErrorBody(400, "The registration token is not a valid FCM registration token", "INVALID_ARGUMENT", "INVALID_ARGUMENT"),
                statusCode: 400)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        var transient = Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
        Assert.Contains("INVALID_ARGUMENT", transient.Reason);
    }

    /// <summary>An auth failure is our own credential's fault - never a device fault, so never a
    /// revocation, the identical guard the RuStore adapter carries for its own `401`/`403`.</summary>
    [Fact]
    public async Task SendAsync_WhenFcmReturns401Unauthenticated_ReturnsTransientFailure_NeverTokenGone()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                new { error = new { code = 401, message = "Request had invalid authentication credentials.", status = "UNAUTHENTICATED" } },
                statusCode: 401)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
    }

    [Fact]
    public async Task SendAsync_WhenFcmReturns503Unavailable_ReturnsTransientFailure()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(
                FcmErrorBody(503, "The service is currently unavailable.", "UNAVAILABLE", "UNAVAILABLE"),
                statusCode: 503)));

        var sender = BuildSender(host.BaseUrl);

        var outcome = await sender.SendAsync(BuildMessage(), CancellationToken.None);

        Assert.IsType<PushSendOutcome.TransientFailure>(outcome);
    }

    [Fact]
    public async Task SendAsync_WhenFcmIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Json(new { })));
        var sender = BuildSender(host.BaseUrl);
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(BuildMessage(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WhenTheResponseHasNoParseableFcmBody_Throws()
    {
        await using var host = await BuildFakeFcmHostAsync(app =>
            app.MapPost("/v1/projects/{projectId}/messages:send", () => Results.Text("<html>502 Bad Gateway</html>", statusCode: 502)));

        var sender = BuildSender(host.BaseUrl);

        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(BuildMessage(), CancellationToken.None));
    }

    private static object FcmErrorBody(int code, string message, string status, string errorCode) => new
    {
        error = new
        {
            code,
            message,
            status,
            details = new[]
            {
                new { @type = "type.googleapis.com/google.firebase.fcm.v1.FcmError", errorCode },
            },
        },
    };

    private static PushMessage BuildMessage() => new(
        DeviceToken: "device-token-abc",
        Title: "New message",
        Body: "Visitor a1b2c3d4 is waiting",
        GroupKey: "ago-conversation-11111111-1111-1111-1111-111111111111",
        TimeToLive: TimeSpan.FromMinutes(5),
        Data: new Dictionary<string, string> { ["conversationId"] = "11111111-1111-1111-1111-111111111111" });

    // Mirrors Ago.Chat.Worker/Program.cs' own composition-root setup for FcmPushSender's typed HttpClient
    // (BaseAddress carries the /v1/projects/{projectId}/ path); the bearer is set per request by the sender
    // from the token provider, so the stub below stands in for the real OAuth2 mint.
    private static FcmPushSender BuildSender(string baseUrl) => new(
        new HttpClient { BaseAddress = new Uri($"{baseUrl}v1/projects/{ProjectId}/") },
        new StubAccessTokenProvider(AccessToken));

    private sealed class StubAccessTokenProvider(string token) : IFcmAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<TestHost> BuildFakeFcmHostAsync(Action<WebApplication> configureRoutes)
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
