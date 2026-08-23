namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: thrown by <see cref="HttpWebhookDeliveryClient"/> when
/// <see cref="WebhookHttpOptions.ResponseHeadersTimeout"/> elapses before an endpoint sends any
/// response headers - the middle one of `resilience.md`'s three layered timeouts (connect,
/// response-headers, total). Deliberately not an <see cref="OperationCanceledException"/> subtype
/// even though it is implemented by cancelling an internal token
/// (<see cref="HttpWebhookDeliveryClient.SendOnceAsync"/>'s own remarks): every catch site in this
/// project's retry loop and circuit breaker treats <see cref="OperationCanceledException"/> as "the
/// caller gave up, never the endpoint's fault, never retried" - a headers timeout is exactly the
/// opposite, a real signal about the endpoint's own behaviour that must flow through the same
/// classify-and-maybe-retry path as a 5xx or a connection failure.
/// </summary>
public sealed class WebhookResponseHeadersTimeoutException()
    : Exception("The webhook endpoint did not send response headers within the configured timeout.");
