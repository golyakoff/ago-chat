using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.YandexGpt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `19-01`: <see cref="YandexGptReplyDraftClient"/>'s own real HTTP boundary, proven against a fake
/// YandexGPT standing in for the real Foundation Models API - `YooKassaPaymentsApiClientTests`' own
/// precedent (a real, in-process, ephemeral-port Kestrel host, not a mocked <see cref="HttpClient"/>),
/// reused here for an LLM provider instead of a payment provider.
///
/// <para><b>This is the wire-level half of `19-01`'s own context-minimalism Done-when</b> - "proven by
/// inspecting the actual request payload in a test, not by code review alone." <see cref="SendsExactlyTheSuppliedMessagesAndTheFixedSystemFraming_NothingElse"/>
/// captures the real JSON bytes this client puts on the wire and asserts they contain nothing beyond
/// the messages it was given plus the one fixed framing string - no site name, no tenant field, no
/// extra message. `GenerateReplyDraftHandlerTests.HandleAsync_OnlySendsThisConversationsOwnMessages_NeverAnotherConversations`
/// is the complementary Application-layer half: that only this conversation's own history ever reaches
/// this client's <paramref name="request"/> in the first place.</para>
///
/// <para><b>What this proves, and what it does not.</b> This proves the exact request shape this
/// codebase sends and that every documented response/error shape this class handles is actually
/// handled the way the code claims - it does <b>not</b> prove any of this against Yandex Cloud's own
/// real service, since this environment has no live API key/folder id to call it with (this item's own
/// report explains why that Done-when box stays unticked, the identical honest limit
/// `YooKassaPaymentsApiClientTests`' own remarks state for ЮKassa).</para>
/// </summary>
public sealed class YandexGptReplyDraftClientTests
{
    [Fact]
    public async Task SendsExactlyTheSuppliedMessagesAndTheFixedSystemFraming_NothingElse()
    {
        string? capturedAuthorization = null;
        string? capturedBody = null;

        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", async (HttpContext context) =>
            {
                capturedAuthorization = context.Request.Headers.Authorization.ToString();
                using var reader = new StreamReader(context.Request.Body);
                capturedBody = await reader.ReadToEndAsync();
                return Results.Json(new
                {
                    result = new
                    {
                        alternatives = new[]
                        {
                            new { message = new { role = "assistant", text = "Yes, we ship to Kazan." }, status = "ALTERNATIVE_STATUS_FINAL" },
                        },
                    },
                });
            }));

        var client = BuildClient(host.BaseUrl, "test-api-key", "folder-42");

        var request = new ReplyDraftGenerationRequest(
        [
            new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "do you ship to Kazan?"),
            new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Operator, "let me check"),
        ]);

        var result = await client.GenerateDraftAsync(request, CancellationToken.None);

        Assert.IsType<ReplyDraftGenerationResult.Success>(result);
        Assert.Equal("Api-Key test-api-key", capturedAuthorization);

        // Every message this client was given is on the wire, verbatim...
        Assert.Contains("\"text\":\"do you ship to Kazan?\"", capturedBody);
        Assert.Contains("\"text\":\"let me check\"", capturedBody);
        // ...folded to the documented user/assistant roles (Visitor -> user, Operator -> assistant)...
        Assert.Contains("\"role\":\"user\",\"text\":\"do you ship to Kazan?\"", capturedBody);
        Assert.Contains("\"role\":\"assistant\",\"text\":\"let me check\"", capturedBody);
        // ...folder-scoped model URI carries the folder id and nothing tenant-specific...
        Assert.Contains("\"modelUri\":\"gpt://folder-42/yandexgpt-lite/latest\"", capturedBody);
        // ...and exactly three messages total: the two supplied plus the one fixed system framing -
        // never a fourth, which is what "nothing else" means on the wire.
        var messageCount = capturedBody!.Split("\"role\":").Length - 1;
        Assert.Equal(3, messageCount);
        Assert.Contains("\"role\":\"system\"", capturedBody);
        // The framing is the same fixed string on every call - not read from any site/tenant
        // configuration, which is the whole point: there is no field in this codebase's own
        // ReplyDraftOptions/YandexGptOptions that could inject a site name here even by mistake.
        Assert.Contains("helping a customer-support operator", capturedBody);
    }

    [Fact]
    public async Task WhenYandexGptAnswersOk_ReturnsTheTrimmedDraftText()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new
                {
                    alternatives = new[]
                    {
                        new { message = new { role = "assistant", text = "  Sure, happy to help.  " }, status = "ALTERNATIVE_STATUS_FINAL" },
                    },
                },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.GenerateDraftAsync(
            new ReplyDraftGenerationRequest([new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "hi")]),
            CancellationToken.None);

        var success = Assert.IsType<ReplyDraftGenerationResult.Success>(result);
        Assert.Equal("Sure, happy to help.", success.DraftText);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    public async Task WhenYandexGptRefusesWithAClientShapedStatus_ThrowsReplyDraftProviderRefusedException(int statusCode)
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(
                new { code = 7, message = "folder does not exist" }, statusCode: statusCode)));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var ex = await Assert.ThrowsAsync<ReplyDraftProviderRefusedException>(() => client.GenerateDraftAsync(
            new ReplyDraftGenerationRequest([new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "hi")]),
            CancellationToken.None));
        Assert.Contains(statusCode.ToString(), ex.Message);
        Assert.Contains("folder does not exist", ex.Message);
    }

    [Fact]
    public async Task WhenYandexGptReturns500_ThrowsHttpRequestException()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.StatusCode(StatusCodes.Status500InternalServerError)));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateDraftAsync(
            new ReplyDraftGenerationRequest([new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "hi")]),
            CancellationToken.None));
    }

    [Fact]
    public async Task WhenYandexGptIsUnreachable_ThrowsARealConnectionFailure()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new { result = new { alternatives = Array.Empty<object>() } })));
        var client = BuildClient(host.BaseUrl, "key", "folder");
        await host.App.StopAsync();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateDraftAsync(
            new ReplyDraftGenerationRequest([new ReplyDraftHistoryMessage(ReplyDraftAuthorKind.Visitor, "hi")]),
            CancellationToken.None));
    }

    private static YandexGptReplyDraftClient BuildClient(string baseUrl, string apiKey, string folderId)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Api-Key", apiKey);
        var options = Options.Create(new YandexGptOptions { ApiKey = apiKey, FolderId = folderId });
        return new YandexGptReplyDraftClient(httpClient, options);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>A real Kestrel host on a real (ephemeral) loopback port, standing in for YandexGPT's own
    /// Foundation Models API - `YooKassaPaymentsApiClientTests`'s own established technique in this
    /// project.</summary>
    private static async Task<TestHost> BuildFakeYandexGptHostAsync(Action<WebApplication> configureRoutes)
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
