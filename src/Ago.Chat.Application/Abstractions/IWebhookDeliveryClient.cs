using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `6-05`: the outbound transport port for one endpoint's delivery of one event -
/// `adr/0013`'s bulkhead, `resilience.md`'s "Inside the dispatcher" list (per-endpoint circuit
/// breaker, per-tenant bulkhead, layered timeouts, bounded retry with backoff and jitter) all live
/// entirely inside whichever <c>Infrastructure</c>-shaped implementation this resolves to - the same
/// "resilience hidden behind the port" shape `Ago.Platform.Caching.Redis.RedisCache` and
/// `Ago.Platform.Storage.S3.S3FileStorage` already established, so this Application-layer handler
/// never references Polly or <c>HttpClient</c> directly (rule 2, `clean-architecture.md`).
///
/// One call = one endpoint's *entire* delivery attempt series for one event, run to a terminal
/// outcome (delivered or dead-lettered) - never partial. The implementing adapter decrypts nothing
/// and knows nothing about <see cref="Domain.WebhookEndpoint.SecretCiphertext"/>; the caller already
/// resolved <paramref name="signingSecret"/> via <see cref="IWebhookSecretCipher.Decrypt"/>, since
/// that port - not this one - is the thing allowed to know a ciphertext exists.
/// </summary>
public interface IWebhookDeliveryClient
{
    Task<WebhookDeliveryAttemptOutcome> DeliverAsync(
        WebhookEndpoint endpoint, string eventType, string payloadJson, string signingSecret, CancellationToken cancellationToken);
}

/// <summary>
/// The terminal result of <see cref="IWebhookDeliveryClient.DeliverAsync"/> - enough for the caller to
/// construct one <see cref="WebhookDelivery"/> row (`6-05`'s own decision, `WebhookDelivery`'s remarks:
/// one summary row per delivery, <see cref="AttemptCount"/> is a count, not a row index).
/// <see cref="Status"/> is never <see cref="WebhookDeliveryStatus.Pending"/> or
/// <see cref="WebhookDeliveryStatus.Failed"/> here - those are useful values for a row *awaiting*
/// dispatch, which this port never returns, since it always runs its own attempts to completion
/// before returning.
/// </summary>
public sealed record WebhookDeliveryAttemptOutcome(
    int AttemptCount, WebhookDeliveryStatus Status, int? ResponseStatus, string? ResponseSnippet);
