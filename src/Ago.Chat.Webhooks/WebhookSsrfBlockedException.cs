namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: thrown by the `SocketsHttpHandler.ConnectCallback` this project's own `Program.cs` wires up
/// when every address a webhook endpoint's hostname resolves to is private, loopback, or link-local
/// (`Ago.Chat.Application.UseCases.RegisterWebhookEndpoint.WebhookUrlValidator.IsDisallowedResolvedAddress`)
/// - the delivery-time recheck `adr/0024` flags as this dispatcher's own obligation, closing the TOCTOU
/// gap `WebhookUrlValidator`'s own registration-time check cannot: a hostname that resolved to a public
/// address when a tenant registered it can resolve to <c>169.254.169.254</c> or an internal address by
/// the time this dispatcher actually connects, and nothing at registration time could have caught that.
///
/// Deliberately not an <see cref="HttpRequestException"/> even though it plays the same "connect
/// failed" role - <see cref="HttpWebhookDeliveryClient.Classify"/> treats an SSRF block as
/// <c>Unexpected</c> (no retry, and it is excluded from circuit-breaker consideration the same way a
/// 4xx is, `WebhookResiliencePipelines.IsBreakerWorthy`) rather than <c>ConnectionGone</c> - a blocked
/// address is a policy decision about *this* endpoint's own registered URL, never evidence the real
/// endpoint is unreachable, and retrying it would just repeat the exact same block every time.
/// </summary>
public sealed class WebhookSsrfBlockedException(string host)
    : Exception($"Every address '{host}' resolved to is private, loopback, or link-local - refusing to connect.");
