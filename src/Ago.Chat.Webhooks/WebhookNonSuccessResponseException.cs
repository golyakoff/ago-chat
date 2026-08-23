namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: thrown by <see cref="HttpWebhookDeliveryClient"/> when an endpoint answers with a non-2xx
/// status, so a non-success response feeds the same catch/classify path as a connection failure or a
/// timeout - one place decides retry-worthiness and circuit-breaker relevance
/// (<see cref="HttpWebhookDeliveryClient.Classify"/>, <see cref="WebhookResiliencePipelines"/>'s own
/// <c>IsBreakerWorthy</c>) regardless of which of the three actually happened.
/// </summary>
public sealed class WebhookNonSuccessResponseException(int statusCode, string? responseSnippet)
    : Exception($"Webhook endpoint responded {statusCode}.")
{
    public int StatusCode { get; } = statusCode;

    public string? ResponseSnippet { get; } = responseSnippet;
}
