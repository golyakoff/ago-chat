namespace Ago.Chat.Domain;

/// <summary>
/// An operation was attempted against a <see cref="WebhookEndpoint"/> in a state that cannot legally
/// perform it - same reasoning as <see cref="InvalidAttachmentStateException"/>: by the time this is
/// reached, Application has already resolved the request (`RevokeWebhookEndpointHandler` treats an
/// already-revoked endpoint as an idempotent no-op *before* calling <see cref="WebhookEndpoint.Revoke"/>,
/// the same "check state, then call, rather than let the domain method's own guard surface as an
/// error" shape `DeleteAttachmentHandler` already uses for `Attachment.MarkDeleted`), so a stale
/// client state is the only way a caller reaches this exception directly.
/// </summary>
public sealed class InvalidWebhookEndpointStateException(string message) : Exception(message);
