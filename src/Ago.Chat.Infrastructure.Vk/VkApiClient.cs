using System.Net.Http.Json;

namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `14-08`: the one class in this codebase that speaks VK's own HTTP shape. Deliberately thin - no
/// retry, no timeout, no circuit breaker (<c>Ago.Chat.Domain.ChannelKind</c>'s adapter, wrapped in
/// <c>Ago.Chat.Module.Channels.ResilientInboundChannelAdapter</c>, is where all four of those live for
/// <see cref="SendMessageAsync"/>); this class is written as if VK always answers, matching
/// <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s own remarks on why an adapter's
/// implementation should never reference the resilience machinery wrapping it.
///
/// <para><b>Every VK call is a POST of form-encoded params, and every response is HTTP 200</b> -
/// confirmed from VK's own SDK (<c>VkDtos.cs</c>'s own honesty note has the full citation). Unlike
/// <c>MaxApiClient</c>/<c>TelegramApiClient</c>, which read the HTTP status code to tell a terminal
/// refusal from a transient fault, this class must always parse the JSON body first: a 200 with an
/// <c>error</c> key is VK's own way of answering both cases at once, and this class is what turns that
/// single shape back into the terminal/transient split <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s
/// own remarks describe.</para>
///
/// <para><see cref="GetGroupInfoAsync"/> and <see cref="GetCallbackConfirmationCodeAsync"/> are outside
/// that split entirely - neither is reached through <c>IInboundChannelAdapter.SendAsync</c>, so neither
/// is wrapped by <c>ResilientInboundChannelAdapter</c>. The first runs once, synchronously, inside
/// <c>VkChannelEndpoints</c>' own connect request (an operator is already waiting on that HTTP response,
/// the same "no pipeline to hide behind" shape <c>TelegramChannelEndpoints.GetMeAsync</c> call already
/// has). The second runs inside <c>VkWebhookEndpoints</c>' own confirmation handler, itself already
/// answering one of VK's own HTTP requests under VK's short delivery timeout - wrapping either in a
/// retrying pipeline would risk answering *later* than the caller needs, which is worse than answering
/// with a clear failure once. Both simply throw <see cref="VkApiCallException"/> on a VK-reported
/// failure and let a genuine transport fault propagate as whatever <see cref="HttpClient"/> throws.</para>
/// </summary>
public sealed class VkApiClient(HttpClient httpClient, string apiVersion)
{
    /// <summary>
    /// <see cref="Application.Abstractions.IInboundChannelAdapter"/>'s own terminal/transient split, made
    /// concrete for VK's real error-code taxonomy - confirmed from VK's own SDK
    /// (<c>ExceptionMapper.php</c>'s own <c>error_code -&gt; exception class</c> table, VkDtos.cs's own
    /// citation). Every code here is one this item could actually confirm exists and means what its name
    /// says; nothing was guessed. <c>5</c> (auth failed - a bad or revoked token), <c>7</c>/<c>15</c>
    /// (permission/access denied), and the messages-specific refusals VK's own SDK names for exactly
    /// this call - <c>900</c> (user blocked messages from the community), <c>901</c> (the user has not
    /// allowed messages from this community), <c>902</c> (blocked by the recipient's own privacy
    /// settings), <c>917</c> (no access to this chat), <c>932</c> ("your community can't interact with
    /// this peer") - are all recipient/credential problems retrying would never fix, the identical
    /// "client-shaped errors are refusals" reasoning <c>MaxApiClient</c>'s own
    /// <c>TerminalRefusalStatusCodes</c> states for HTTP status instead of a body code. Everything else -
    /// including <c>6</c>/<c>9</c>/<c>29</c> (rate limiting/flood control) and any code this item did not
    /// confirm - defaults to transient (thrown), the same "err toward retrying, not toward silently
    /// giving up on a real outage" default MAX's own class applies to an unrecognised status.
    /// </summary>
    private static readonly HashSet<int> TerminalRefusalErrorCodes = [5, 7, 15, 900, 901, 902, 917, 932];

    public async Task<VkSendResult> SendMessageAsync(
        string token, long groupId, long peerId, string text, long randomId, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["access_token"] = token,
            ["v"] = apiVersion,
            ["group_id"] = groupId.ToString(),
            ["peer_id"] = peerId.ToString(),
            ["message"] = text,
            ["random_id"] = randomId.ToString(),
        };

        using var response = await httpClient.PostAsync("messages.send", new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<VkSendMessageEnvelope>(cancellationToken);

        if (envelope?.Error is { } error)
        {
            if (TerminalRefusalErrorCodes.Contains(error.ErrorCode))
            {
                return VkSendResult.Refused($"VK refused the message (error {error.ErrorCode}): {Truncate(error.ErrorMsg)}");
            }

            throw new HttpRequestException(
                $"VK API returned error {error.ErrorCode} for messages.send: {Truncate(error.ErrorMsg)}");
        }

        if (envelope?.Response is { } messageId)
        {
            return VkSendResult.Sent(messageId.ToString());
        }

        throw new HttpRequestException("VK API returned neither a response nor an error for messages.send.");
    }

    /// <summary>
    /// <c>groups.getById</c>, called with no <c>group_id</c> so VK resolves it from the calling token's
    /// own identity - <c>VkChannelEndpoints</c>' own connect-time validation step, doing double duty the
    /// same way <c>TelegramChannelEndpoints.GetMeAsync</c> does: it proves the token actually works
    /// (VK rejects a bad or already-revoked token here, immediately, rather than the first time an
    /// operator tries to reply), and it discovers the community's own numeric id - the value
    /// <c>ChannelCredential.ProviderAccountId</c> exists to hold, because <see cref="SendMessageAsync"/>
    /// needs it on every single send and re-discovering it on every send would be an extra VK call (and
    /// an extra failure point inside the resilience-wrapped hot path) for a value that never changes
    /// once a community's own token is issued.
    /// </summary>
    public async Task<VkGroupInfo> GetGroupInfoAsync(string token, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string> { ["access_token"] = token, ["v"] = apiVersion };

        using var response = await httpClient.PostAsync("groups.getById", new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<VkGroupsGetByIdEnvelope>(cancellationToken);

        if (envelope?.Error is { } error)
        {
            throw new VkApiCallException($"VK rejected the token (error {error.ErrorCode}): {Truncate(error.ErrorMsg)}");
        }

        var group = envelope?.Response?.Groups?.FirstOrDefault();
        if (group is null)
        {
            throw new VkApiCallException("VK's groups.getById returned no community for this token.");
        }

        return new VkGroupInfo(group.Id, group.Name);
    }

    /// <summary>
    /// <c>groups.getCallbackConfirmationCode</c> - VK's own answer to "what string does my server need
    /// to echo back to prove it owns this callback URL", fetched fresh rather than persisted
    /// (<c>VkWebhookEndpoints</c>' own remarks on why: the code is deterministic per community and does
    /// not change, so there is nothing this call needs to be idempotent-with-itself against, and it is
    /// called at most once per real VK confirmation attempt - not a hot path worth caching for).
    /// </summary>
    public async Task<string> GetCallbackConfirmationCodeAsync(string token, long groupId, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["access_token"] = token,
            ["v"] = apiVersion,
            ["group_id"] = groupId.ToString(),
        };

        using var response = await httpClient.PostAsync(
            "groups.getCallbackConfirmationCode", new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<VkGetCallbackConfirmationCodeEnvelope>(cancellationToken);

        if (envelope?.Error is { } error)
        {
            throw new VkApiCallException(
                $"VK rejected the confirmation-code request (error {error.ErrorCode}): {Truncate(error.ErrorMsg)}");
        }

        if (envelope?.Response?.Code is { Length: > 0 } code)
        {
            return code;
        }

        throw new VkApiCallException("VK's groups.getCallbackConfirmationCode returned no code.");
    }

    private static string Truncate(string? text) =>
        text is null ? "(no message)" : text.Length > 500 ? text[..500] : text;
}

public sealed record VkSendResult(bool Success, string? ProviderMessageId, string? RefusalReason)
{
    public static VkSendResult Sent(string? providerMessageId) => new(true, providerMessageId, null);

    public static VkSendResult Refused(string reason) => new(false, null, reason);
}

public sealed record VkGroupInfo(long GroupId, string? Name);
