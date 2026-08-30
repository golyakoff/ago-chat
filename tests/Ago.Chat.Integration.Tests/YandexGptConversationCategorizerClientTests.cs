using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
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
/// `19-02`: <see cref="YandexGptConversationCategorizerClient"/>'s own real HTTP boundary, the identical
/// technique <see cref="YandexGptReplyDraftClientTests"/> establishes for `19-01` - a real, in-process,
/// ephemeral-port Kestrel host standing in for YandexGPT, not a mocked <see cref="HttpClient"/>.
///
/// <para><b>The one behaviour this class proves that its `19-01` counterpart does not need to</b>: the
/// first half of this item's own "never invent a tag" defence in depth -
/// <see cref="TheClientOnlyEverReturnsTagIdsFromTheCandidateSet_NeverAnInventedName"/> and
/// <see cref="AMalformedOrUnparseableAnswer_ThrowsRatherThanReturningAnEmptyResult"/> prove the client
/// itself discards or rejects anything outside the given candidate list, never trusting the provider's
/// own text at face value.</para>
///
/// <para><b>What this proves, and what it does not</b> - the identical honest limit
/// <see cref="YandexGptReplyDraftClientTests"/>'s own remarks state: this environment has no live
/// YandexGPT API key/folder id, so nothing here is verified against Yandex Cloud's own real service.
/// </para>
/// </summary>
public sealed class YandexGptConversationCategorizerClientTests
{
    private static readonly CategorizationCandidateTag Billing = new(new TagId(Guid.NewGuid()), "Billing");
    private static readonly CategorizationCandidateTag Shipping = new(new TagId(Guid.NewGuid()), "Shipping");

    [Fact]
    public async Task SendsExactlyTheSuppliedMessagesAndCandidateTags_NothingElse()
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
                    result = new { alternatives = new[] { new { message = new { role = "assistant", text = "[\"Shipping\"]" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
                });
            }));

        var client = BuildClient(host.BaseUrl, "test-api-key", "folder-42");

        var request = new CategorizationRequest(
            [
                new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "do you ship to Kazan?"),
                new CategorizationHistoryMessage(CategorizationAuthorKind.Operator, "let me check"),
            ],
            [Billing, Shipping]);

        var result = await client.CategorizeAsync(request, CancellationToken.None);

        Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal("Api-Key test-api-key", capturedAuthorization);
        Assert.Contains("\"text\":\"do you ship to Kazan?\"", capturedBody);
        Assert.Contains("\"text\":\"let me check\"", capturedBody);
        Assert.Contains("\"role\":\"user\",\"text\":\"do you ship to Kazan?\"", capturedBody);
        Assert.Contains("\"role\":\"assistant\",\"text\":\"let me check\"", capturedBody);
        Assert.Contains("\"modelUri\":\"gpt://folder-42/yandexgpt-lite/latest\"", capturedBody);
        // Both candidate tag names are on the wire, and no third one - the allowed-tags framing message.
        Assert.Contains("Billing", capturedBody!);
        Assert.Contains("Shipping", capturedBody!);
        // Exactly four messages: two system (fixed framing + allowed-tags list) plus the two supplied.
        var messageCount = capturedBody!.Split("\"role\":").Length - 1;
        Assert.Equal(4, messageCount);
    }

    [Fact]
    public async Task WhenYandexGptAnswersWithAMatchingTagName_ReturnsItsId()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "[\"Billing\"]" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing, Shipping]),
            CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal([Billing.TagId], success.TagIds);
    }

    [Fact]
    public async Task WhenYandexGptAnswersWithAnEmptyArray_ReturnsAnEmptySuccess_NotAnError()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "[]" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        Assert.Empty(success.TagIds);
    }

    /// <summary>The first half of this item's own "never invent a tag" defence in depth
    /// (`YandexGptConversationCategorizerClient`'s own remarks; `CategorizeConversationHandler.ApplyAsync`
    /// is the second). A name the model returns that does not match any candidate - a genuine invention,
    /// a typo, a different casing of something not actually offered - is silently dropped rather than
    /// surfaced as a tag id the caller would have to notice was wrong.</summary>
    [Fact]
    public async Task TheClientOnlyEverReturnsTagIdsFromTheCandidateSet_NeverAnInventedName()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "[\"Billing\", \"Refunds\", \"refund\"]" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        // "Billing" matched (case-sensitive-exact); "Refunds" and "refund" name nothing in the
        // candidate set and are both dropped, not surfaced as ids.
        Assert.Equal([Billing.TagId], success.TagIds);
    }

    /// <summary>Case-insensitive matching against the real candidate name - a model that answers
    /// "billing" instead of "Billing" still resolves to the real tag rather than being dropped as if it
    /// had invented one, the deliberate leniency <see cref="YandexGptConversationCategorizerClient"/>'s
    /// own remarks state a reason for.</summary>
    [Fact]
    public async Task MatchesCandidateNamesCaseInsensitively()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "[\"billing\"]" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal([Billing.TagId], success.TagIds);
    }

    /// <summary>A model wrapping its own answer in a markdown code fence (a documented, common habit
    /// even when told not to) still parses - <see cref="YandexGptConversationCategorizerClient"/>'s own
    /// remarks.</summary>
    [Fact]
    public async Task StripsAMarkdownCodeFenceAroundTheJsonArray()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "```json\n[\"Billing\"]\n```" }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var result = await client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None);

        var success = Assert.IsType<CategorizationResult.Success>(result);
        Assert.Equal([Billing.TagId], success.TagIds);
    }

    /// <summary>A 2xx answer whose text is not a JSON array of strings at all - prose, a JSON object -
    /// is treated as malformed/transient (a thrown <see cref="HttpRequestException"/>), never silently
    /// turned into an empty <see cref="CategorizationResult.Success"/> that would look identical to "the
    /// model judged nothing applies" (this class's own remarks on why that distinction matters).
    /// </summary>
    [Fact]
    public async Task AMalformedOrUnparseableAnswer_ThrowsRatherThanReturningAnEmptyResult()
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(new
            {
                result = new { alternatives = new[] { new { message = new { role = "assistant", text = "Sure, I'd tag this as Billing." }, status = "ALTERNATIVE_STATUS_FINAL" } } },
            })));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    public async Task WhenYandexGptRefusesWithAClientShapedStatus_ThrowsConversationCategorizationProviderRefusedException(int statusCode)
    {
        await using var host = await BuildFakeYandexGptHostAsync(app =>
            app.MapPost("completion", () => Results.Json(
                new { code = 7, message = "folder does not exist" }, statusCode: statusCode)));

        var client = BuildClient(host.BaseUrl, "key", "folder");

        var ex = await Assert.ThrowsAsync<ConversationCategorizationProviderRefusedException>(() => client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
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

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CategorizeAsync(
            new CategorizationRequest([new CategorizationHistoryMessage(CategorizationAuthorKind.Visitor, "hi")], [Billing]),
            CancellationToken.None));
    }

    private static YandexGptConversationCategorizerClient BuildClient(string baseUrl, string apiKey, string folderId)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Api-Key", apiKey);
        var options = Options.Create(new CategorizationYandexGptOptions { ApiKey = apiKey, FolderId = folderId });
        return new YandexGptConversationCategorizerClient(httpClient, options);
    }

    private sealed record TestHost(WebApplication App, string BaseUrl) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

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
