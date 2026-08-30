using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-02`: the second class in this project that speaks YandexGPT's own Foundation Models HTTP shape -
/// same thin, no-retry-of-its-own discipline <see cref="YandexGptReplyDraftClient"/>'s own remarks state
/// (<c>Ago.Chat.Module.ChatModule</c> builds this class's <see cref="HttpClient"/>, including the
/// `Api-Key` header). Implements <see cref="IConversationCategorizer"/>, the provider-neutral Application
/// port established for this item; nothing above this project may reference this class.
///
/// <para><b>The framing, stated in full.</b> A fixed system instruction plus one more system-role
/// message naming exactly this call's own <see cref="CategorizationRequest.CandidateTags"/> - no site
/// name, no other conversation's data, nothing beyond what the request itself carries. The instruction
/// asks for a JSON array of tag names, copied verbatim from the allowed list, or an empty array if none
/// apply - the model is told directly not to invent, rename, or translate a name.</para>
///
/// <para><b>"Never invent a tag", enforced here rather than trusted from the prompt.</b> A prompt
/// instruction is not a guarantee - <see cref="ParseTagIds"/> matches every name the model returns
/// against <paramref name="request"/>'s own candidate list (case-insensitively, since a model
/// normalising case is a likely failure mode a strict match would punish for no reason) and silently
/// drops anything that does not match. This is the first half of this item's own defence in depth;
/// <c>CategorizeConversationHandler.ApplyAsync</c>'s identical re-check against the same candidate list
/// is the second (that handler's own remarks).</para>
///
/// <para><b>The terminal/transient split</b>, identical to <see cref="YandexGptReplyDraftClient"/>'s own:
/// a client-shaped refusal (400/401/403/404) throws <see cref="ConversationCategorizationProviderRefusedException"/>;
/// everything else throws <see cref="HttpRequestException"/>, including a 2xx response whose text does
/// not parse as a JSON array of strings - an unparseable answer is treated as transient/unexpected
/// rather than silently becoming an empty (and therefore indistinguishable from "the model judged
/// nothing applies") result. <b>Not confirmed against a real YandexGPT response</b> - see this item's
/// own report for exactly which claims are asserted here versus verified, the identical honest limit
/// <see cref="YandexGptDtos"/>' own remarks state for `19-01`.</para>
/// </summary>
public sealed class YandexGptConversationCategorizerClient(
    HttpClient httpClient, IOptions<CategorizationYandexGptOptions> options) : IConversationCategorizer
{
    private const string SystemPrompt =
        "You are helping classify a customer-support conversation into zero or more categories from a " +
        "fixed list a human already defined. Read the conversation below, then the line starting " +
        "'Allowed tags:' that gives the only names you may choose from, as a JSON array. Reply with a " +
        "JSON array containing the subset of those exact names that apply to this conversation - copy " +
        "each name exactly as given, case-sensitive, one array entry per matching tag. If none apply, " +
        "reply with an empty array: []. Never invent, rename, translate, or abbreviate a tag name - only " +
        "the names given may appear in your answer. Reply with the JSON array only - no explanation, no " +
        "markdown code fence, no other text.";

    private static readonly HttpStatusCode[] TerminalRefusalStatusCodes =
    [
        HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
    ];

    public async Task<CategorizationResult> CategorizeAsync(
        CategorizationRequest request, CancellationToken cancellationToken)
    {
        var opts = options.Value;

        var allowedTagsJson = JsonSerializer.Serialize(request.CandidateTags.Select(t => t.Name));
        var messages = new List<YandexGptMessage>
        {
            new("system", SystemPrompt),
            new("system", $"Allowed tags: {allowedTagsJson}"),
        };
        messages.AddRange(request.RecentMessages.Select(m =>
            new YandexGptMessage(m.AuthorKind == CategorizationAuthorKind.Visitor ? "user" : "assistant", m.Body)));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completion")
        {
            Content = JsonContent.Create(new YandexGptCompletionRequest(
                ModelUri: $"gpt://{opts.FolderId}/{opts.ModelName}/latest",
                CompletionOptions: new YandexGptCompletionOptions(
                    Stream: false, Temperature: 0.0, MaxTokens: opts.MaxTokens.ToString()),
                Messages: messages)),
        };

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<YandexGptCompletionResponse>(cancellationToken);
            var rawText = body?.Result?.Alternatives?.FirstOrDefault()?.Message?.Text;
            if (string.IsNullOrWhiteSpace(rawText))
            {
                throw new HttpRequestException(
                    $"YandexGPT completion returned {(int)response.StatusCode} with no alternative text.");
            }

            return new CategorizationResult.Success(ParseTagIds(rawText, request.CandidateTags));
        }

        if (TerminalRefusalStatusCodes.Contains(response.StatusCode))
        {
            var error = await response.Content.ReadFromJsonAsync<YandexGptErrorResponse>(cancellationToken);
            throw new ConversationCategorizationProviderRefusedException(
                $"YandexGPT refused the completion ({(int)response.StatusCode}): {error?.Message ?? "no reason given"}");
        }

        var transientErrorText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"YandexGPT API returned {(int)response.StatusCode} for POST completion: {Truncate(transientErrorText)}",
            null, response.StatusCode);
    }

    /// <summary>Best-effort extraction of a JSON string array from the model's own free-text answer -
    /// tolerant of a markdown code fence around it (a documented, common LLM habit even when told not
    /// to), strict about everything else: a response that still does not parse into a JSON array of
    /// strings is treated as malformed rather than guessed at (this class's own remarks on why that is a
    /// thrown, transient-shaped exception, not an empty result).</summary>
    private static IReadOnlyList<TagId> ParseTagIds(string rawText, IReadOnlyList<CategorizationCandidateTag> candidates)
    {
        var trimmed = StripMarkdownFence(rawText.Trim());

        List<string>? names;
        try
        {
            names = JsonSerializer.Deserialize<List<string>>(trimmed);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException(
                $"YandexGPT categorization answer did not parse as a JSON array of tag names: {Truncate(rawText)}", ex);
        }

        if (names is null)
        {
            throw new HttpRequestException(
                $"YandexGPT categorization answer parsed to null, not a JSON array: {Truncate(rawText)}");
        }

        // Case-insensitive match against exactly this call's own candidates - anything that does not
        // match a real candidate name is silently dropped, never surfaced as an "invented tag" the
        // caller would have to notice and filter itself (this class's own remarks, first half of the
        // "never invent a tag" defence in depth).
        var byName = candidates.ToDictionary(c => c.Name, c => c.TagId, StringComparer.OrdinalIgnoreCase);
        var matched = new List<TagId>();
        foreach (var name in names)
        {
            if (byName.TryGetValue(name, out var tagId) && !matched.Contains(tagId))
            {
                matched.Add(tagId);
            }
        }

        return matched;
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        var withoutOpeningFence = firstNewline >= 0 ? text[(firstNewline + 1)..] : text;
        var closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
        return closingFenceIndex >= 0 ? withoutOpeningFence[..closingFenceIndex].Trim() : withoutOpeningFence.Trim();
    }

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
