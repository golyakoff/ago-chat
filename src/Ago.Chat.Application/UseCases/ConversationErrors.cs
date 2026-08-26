using System.Globalization;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>Error codes shared by the conversation use cases - kept in one place so a client
/// branching on <c>type</c> (api-design.md) sees the same code regardless of which use case raised
/// it.
///
/// `4-05`: public, not internal - <c>Ago.Chat.Api</c>'s pipeline (<c>MessageBatchWriter</c>,
/// <c>ChannelMessagePipeline</c>) constructs the same error vocabulary the synchronous handlers
/// used to build directly, since the wire contract callers see must not change depending on whether
/// a send took the pipeline's queued path. Infrastructure/Host referencing an Application type is
/// the dependency rule working as intended (clean-architecture.md) - the alternative, duplicating
/// these codes in `Ago.Chat.Api`, would let the two vocabularies drift apart silently.</summary>
public static class ConversationErrors
{
    public static Error NotFound(Guid conversationId) =>
        new("Conversation.NotFound", $"Conversation {conversationId} was not found.");

    public static Error Forbidden(string reason) =>
        new("Conversation.Forbidden", reason);

    public static Error InvalidState(string reason) =>
        new("Conversation.InvalidState", reason);

    /// <summary>`6-08`: a second `DbUpdateConcurrencyException` on the same request, after the handler
    /// already reloaded the row once and retried against it - a third writer landed inside that narrow
    /// retry window. Distinct from <see cref="InvalidState"/> (a real business-state conflict the
    /// domain method itself detected on fresh data) - this is "the row would not sit still long enough
    /// to save," so the caller's own remedy is genuinely "retry the whole request," not "you did
    /// something wrong."</summary>
    public static Error ConcurrencyConflict(Guid conversationId) =>
        new("Conversation.ConcurrencyConflict", $"Conversation {conversationId} was modified concurrently; retry the request.");

    public static Error InvalidBody(string reason) =>
        new("Message.InvalidBody", reason);

    /// <summary>`14-06`: a structured payload that is malformed, oversized, or offers more actions
    /// than a text channel could present. Its own code rather than <see cref="InvalidBody"/>'s
    /// because a client that can retry with shorter prose cannot retry with shorter prose here - the
    /// two failures have different remedies, which is the only reason a second code earns its
    /// place. <b>Never a failure about what the payload means</b>: AGO Chat owns no schema for
    /// it.</summary>
    public static Error InvalidContent(string reason) =>
        new("Message.InvalidContent", reason);

    // The retry-after rides in the message text, not a structured field - Error only ever carries
    // Code+Message (Ago.Platform.Kernel), and every caller of this handler already just forwards
    // Error.Message verbatim (VisitorHub's HubException, matching every other failure here).
    // InvariantCulture, not the current culture - found by running the test suite on a machine
    // whose culture formats a decimal point as a comma, turning "5.0s" into "5,0s".
    public static Error RateLimited(TimeSpan retryAfter) =>
        new("Message.RateLimited", $"Too many messages - retry after {retryAfter.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");

    /// <summary>`4-05`: the pipeline's own failure modes - a full channel that stayed full past
    /// <c>MessagePipelineOptions.EnqueueTimeout</c>, a batch whose commit failed, or a send that
    /// arrived after the channel closed for shutdown. Distinct from <see cref="RateLimited"/> (a
    /// policy decision about this caller) - this is "the server itself could not take the work right
    /// now," always safe to retry.</summary>
    public static Error Unavailable(string reason) =>
        new("Message.Unavailable", reason);

    // `5-03`: attachments get their own codes under the same shared vocabulary, rather than a
    // separate error class - Message.InvalidBody/RateLimited above already established that this
    // file holds every use case's errors, not just Conversation's own, so a client branching on
    // `type` (api-design.md) has exactly one place to look regardless of which use case raised it.
    public static Error AttachmentNotFound(Guid attachmentId) =>
        new("Attachment.NotFound", $"Attachment {attachmentId} was not found.");

    public static Error AttachmentInvalidContentType(string contentType) =>
        new("Attachment.InvalidContentType", $"Content type '{contentType}' is not allowed.");

    public static Error AttachmentTooLarge(long declaredSizeBytes, long maxSizeBytes) =>
        new("Attachment.TooLarge", $"Declared size {declaredSizeBytes} bytes exceeds the {maxSizeBytes}-byte limit.");

    /// <summary>The client's "uploaded" claim did not survive a HEAD-verify against the real object -
    /// no real upload found, or its size/content-type does not match what was declared at presign
    /// time. The attachment itself stays `Pending` (<see cref="Domain.Attachment.ConfirmReady"/>), so
    /// this is always safe to retry once the real upload lands.</summary>
    public static Error AttachmentVerificationFailed(string reason) =>
        new("Attachment.VerificationFailed", reason);

    public static Error AttachmentNotReady(string reason) =>
        new("Attachment.NotReady", reason);

    // `6-03`: same shared vocabulary, same reason - one place a client branching on `type` looks,
    // regardless of which use case raised it.
    public static Error WebhookEndpointNotFound(Guid webhookEndpointId) =>
        new("WebhookEndpoint.NotFound", $"Webhook endpoint {webhookEndpointId} was not found.");

    public static Error WebhookInvalidUrl(string reason) =>
        new("WebhookEndpoint.InvalidUrl", reason);

    // `10-02`: same shared vocabulary, same reason - one place a client branching on `type` looks.
    /// <summary>The caller's `sub` already resolves to an `operators` row - "one registration per
    /// identity for Stage 10" (`10-02-site-and-operator-registration.md`'s own Scope). A real `409`,
    /// not a validation error: the request is well-formed, the identity is real, it just already has
    /// a site.</summary>
    public static Error SiteAlreadyRegistered() =>
        new("Site.AlreadyRegistered", "This identity has already registered a site.");

    public static Error SiteInvalidName(string reason) =>
        new("Site.InvalidName", reason);

    public static Error SiteInvalidOrigin(string reason) =>
        new("Site.InvalidOrigin", reason);

    /// <summary>Distinct code from <see cref="RateLimited"/> even though the shape is identical -
    /// `10-02`'s own per-`sub`/per-IP buckets are a different resource than `SendVisitorMessage`'s, and
    /// a client branching on `type` should be able to tell "you are sending too fast" apart from "you
    /// are registering too fast" without parsing the message text.</summary>
    public static Error SiteRegistrationRateLimited(TimeSpan retryAfter) =>
        new("Site.RateLimited", $"Too many registration attempts - retry after {retryAfter.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");

    // `11-01`: same shared vocabulary, same reason - GetWidgetConfigHandler/UpdateWidgetConfigHandler
    // add their own codes here rather than a separate error class, matching every use case since 4-05.
    public static Error SiteNotFound(Guid siteId) =>
        new("Site.NotFound", $"Site {siteId} was not found.");

    /// <summary>The hex format `WidgetConfig`'s own constructor rejected, surfaced as a client error
    /// rather than an unhandled exception - `UpdateWidgetConfigHandler` catches the `ArgumentException`
    /// and translates it here, the same "validate the value object, translate the throw at the
    /// Application boundary" split its own remarks describe.</summary>
    public static Error WidgetConfigInvalidColor(string reason) =>
        new("WidgetConfig.InvalidColor", reason);

    public static Error WidgetConfigInvalidPosition(string reason) =>
        new("WidgetConfig.InvalidPosition", reason);
}
