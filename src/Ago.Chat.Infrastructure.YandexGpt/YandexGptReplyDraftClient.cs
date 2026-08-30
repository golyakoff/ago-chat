using System.Net;
using System.Net.Http.Json;
using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-01`: the one class in this codebase that speaks YandexGPT's own Foundation Models HTTP shape -
/// the same "thin, no retry/timeout/circuit-breaker of its own" discipline `YooKassaPaymentsApiClient`'s
/// own remarks establish (that is `Ago.Chat.Module.ChatModule`'s job when it builds this class's
/// <see cref="HttpClient"/>, including the `Api-Key` auth header - see `ChatModule`'s own remarks).
/// Implements <see cref="IReplyDraftGenerator"/>, the provider-neutral Application port; nothing above
/// this project may reference this class or any type in this file directly.
///
/// <para><b>The system framing, stated in full because it is the entire "minimal framing" half of this
/// item's own context-minimalism rule.</b> Below is the only text this client adds beyond what
/// <see cref="ReplyDraftGenerationRequest.RecentMessages"/> already carries - no site name, no tenant
/// policy, no product catalog, nothing `19-03`'s own future knowledge-base scope would need. It asks
/// for a short, polite draft reply in the visitor's own language and nothing else.</para>
///
/// <para><b>The terminal/transient split, made concrete for YandexGPT.</b> A client-shaped refusal
/// (400/401/403/404 - a malformed request, an expired/revoked key, a folder the key cannot use) throws
/// <see cref="ReplyDraftProviderRefusedException"/>; everything else (429, 5xx, a network fault) throws
/// <see cref="HttpRequestException"/> - the identical reasoned default `YooKassaPaymentsApiClient`'s own
/// remarks state, applied here to a third provider. <b>Not confirmed against a real YandexGPT
/// response</b> - see <see cref="YandexGptDtos"/>' own remarks on why.</para>
/// </summary>
public sealed class YandexGptReplyDraftClient(HttpClient httpClient, IOptions<YandexGptOptions> options) : IReplyDraftGenerator
{
    private const string SystemPrompt =
        "You are helping a customer-support operator answer a chat conversation. Read the recent " +
        "messages below and draft one short, polite reply the operator could send next. Reply with " +
        "the suggested message text only - no explanation, no greeting the operator, no quotation " +
        "marks around it. Write in the same language the visitor is using.";

    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<ReplyDraftGenerationResult> GenerateDraftAsync(
        ReplyDraftGenerationRequest request, CancellationToken cancellationToken)
    {
        var opts = options.Value;

        var messages = new List<YandexGptMessage> { new("system", SystemPrompt) };
        messages.AddRange(request.RecentMessages.Select(m =>
            new YandexGptMessage(m.AuthorKind == ReplyDraftAuthorKind.Visitor ? "user" : "assistant", m.Body)));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completion")
        {
            Content = JsonContent.Create(new YandexGptCompletionRequest(
                ModelUri: $"gpt://{opts.FolderId}/{opts.ModelName}/latest",
                CompletionOptions: new YandexGptCompletionOptions(
                    Stream: false, Temperature: 0.3, MaxTokens: opts.MaxTokens.ToString()),
                Messages: messages)),
        };

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<YandexGptCompletionResponse>(cancellationToken);
            var draftText = body?.Result?.Alternatives?.FirstOrDefault()?.Message?.Text;
            if (string.IsNullOrWhiteSpace(draftText))
            {
                // YandexGPT answered 2xx but did not include the one field this call exists to get -
                // not a documented refusal shape, so this is treated as a transient/unexpected
                // condition rather than a silent empty draft reaching the composer
                // (`YooKassaPaymentsApiClient`'s own identical choice for a missing `confirmation_url`).
                throw new HttpRequestException(
                    $"YandexGPT completion returned {(int)response.StatusCode} with no alternative text.");
            }

            return new ReplyDraftGenerationResult.Success(draftText.Trim());
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var error = await response.Content.ReadFromJsonAsync<YandexGptErrorResponse>(cancellationToken);
            throw new ReplyDraftProviderRefusedException(
                $"YandexGPT refused the completion ({(int)response.StatusCode}): {error?.Message ?? "no reason given"}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"YandexGPT API returned {(int)response.StatusCode} for POST completion: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
