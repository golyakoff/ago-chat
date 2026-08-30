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
    /// <summary>
    /// `10-02`'s original meaning: the caller's `sub` already resolved to an `operators` row anywhere
    /// - "one registration per identity for Stage 10" (`10-02-site-and-operator-registration.md`'s
    /// own Scope). `13-07`/`adr/0068` removed the pre-check that produced this from that path (an
    /// identity may now register more than one `Site`), so today this is raised only by
    /// <c>RegisterSiteHandler</c>'s own defensive check on
    /// <see cref="ISiteRegistrationRepository.TryRegisterAsync"/> returning <see langword="false"/> -
    /// effectively unreachable in ordinary operation once `siteId` is freshly generated on every call
    /// (that handler's own remarks explain why), kept as a `409` rather than a `500` for the same
    /// reason it always was: a unique-index violation on this specific pair is still "you already have
    /// this", not a server error.
    /// </summary>
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

    /// <summary>`11-10`: the closed `Locale` set `UpdateWidgetConfigHandler`'s own `Enum.TryParse`/
    /// `Enum.IsDefined` check rejected - the same "validate the enum, translate the miss at the
    /// Application boundary" split `WidgetConfigInvalidPosition` already draws for `Position`.</summary>
    public static Error WidgetConfigInvalidLocale(string reason) =>
        new("WidgetConfig.InvalidLocale", reason);

    /// <summary>`16-04`: whitespace-only or over-length notice text `WidgetConfig`'s own constructor
    /// rejected - the same catch-and-translate split `WidgetConfigInvalidColor` already draws, kept as
    /// its own code (not folded into that one) so a console or API caller can tell which field to
    /// fix.</summary>
    public static Error WidgetConfigInvalidNoticeText(string reason) =>
        new("WidgetConfig.InvalidNoticeText", reason);

    /// <summary>`16-04`: a notice URL that is not an absolute `https://` URL - `WidgetConfig`'s own
    /// constructor rejected it (its own remarks explain why this reuses only the scheme-only reflex
    /// `6-03`'s webhook validator applies, not its SSRF check).</summary>
    public static Error WidgetConfigInvalidNoticeUrl(string reason) =>
        new("WidgetConfig.InvalidNoticeUrl", reason);

    /// <summary>`14-04`: an offline auto-reply configuration `OfflineAutoReplyRule`/
    /// `OfflineAutoReplySettings` refused - an empty or oversized keyword or reply, too many rules, or
    /// an enabled configuration with no fallback text. One code rather than five, because every one of
    /// them has the same remedy (fix the field the message names) and the message carries the
    /// detail.</summary>
    public static Error OfflineAutoReplyInvalid(string reason) =>
        new("OfflineAutoReply.Invalid", reason);

    /// <summary>`18-03`: a canned response `CannedResponse` refused - an empty or oversized title or
    /// body, or too many in the list. Same "one code, the message carries the detail" reasoning
    /// `OfflineAutoReplyInvalid` states for itself.</summary>
    public static Error CannedResponseInvalid(string reason) =>
        new("CannedResponse.Invalid", reason);

    // `14-02`: same shared vocabulary, same reason - RegisterChannelCredentialHandler/
    // RevokeChannelCredentialHandler add their own codes here rather than a separate error class.
    public static Error ChannelCredentialNotFound(Guid channelCredentialId) =>
        new("ChannelCredential.NotFound", $"Channel credential {channelCredentialId} was not found.");

    /// <summary>`adr/0069`'s "one bot per tenant per channel" - raised when an active credential
    /// already exists for the (site, kind) pair the caller is trying to register. The remedy is
    /// `RevokeChannelCredential` first, matching `WebhookEndpoint`'s own revoke-and-recreate-only
    /// shape - never a silent overwrite of a token that might still be in use.</summary>
    public static Error ChannelAlreadyConnected(string reason) =>
        new("ChannelCredential.AlreadyConnected", reason);

    public static Error ChannelInvalidToken(string reason) =>
        new("ChannelCredential.InvalidToken", reason);

    /// <summary>`14-08`: this deployment has not configured the public URL VK's Callback API would need
    /// to reach it - unlike MAX (which degrades to a local long-poll loop when unconfigured) or Telegram
    /// (which never needed a public URL at all), VK's webhook is this channel's only inbound mechanism,
    /// so with no URL to hand VK there is genuinely nothing a connect attempt could complete. `503`, not
    /// `400`: the caller's request is fine, this deployment is the thing not ready
    /// (<c>DemoEndpoints</c>' own "a clear 'not enabled here' rather than a 404 that reads like a bug"
    /// precedent, applied to the one channel that cannot silently fall back to polling).</summary>
    public static Error ChannelNotAvailable(string reason) =>
        new("ChannelCredential.NotAvailable", reason);

    // `13-01`: same shared vocabulary, same reason - CreateOperatorInviteHandler/RedeemOperatorInviteHandler
    // add their own codes here rather than a separate error class.
    public static Error OperatorInviteInvalidRole(string reason) =>
        new("OperatorInvite.InvalidRole", reason);

    /// <summary>No invite matches the presented code - never generated, or the caller mistyped it.
    /// Deliberately the same response whether the code truly never existed or belongs to a different
    /// site than the caller assumed - <c>RedeemOperatorInviteHandler</c>'s own remarks on why this
    /// route (gated by `RequireKeycloakIdentity`, not `RequireOperatorIdentity`) has no `SiteId` to
    /// scope an info-hiding check against in the first place; the code itself is the only key.</summary>
    public static Error OperatorInviteNotFound() =>
        new("OperatorInvite.NotFound", "No operator invite matches this code.");

    /// <summary>A real invite that once existed, past its own `expires_at` - `410 Gone`, not `404`,
    /// because the distinction is genuinely useful to a caller: a mistyped code should be tried again
    /// carefully, an expired one should be asked for a fresh invite instead.</summary>
    public static Error OperatorInviteExpired() =>
        new("OperatorInvite.Expired", "This operator invite has expired.");

    public static Error OperatorInviteAlreadyRedeemed() =>
        new("OperatorInvite.AlreadyRedeemed", "This operator invite has already been redeemed.");

    /// <summary>`13-07`/`adr/0068`'s own adjustment: the redeeming identity already resolves to an
    /// `Operator` row on *this invite's own* site - never "resolves to an operator row anywhere", the
    /// older, superseded rule this item's own backlog note was corrected away from once `13-07`
    /// shipped.</summary>
    public static Error OperatorInviteAlreadyOperatorOnSite() =>
        new("OperatorInvite.AlreadyOperatorOnSite", "This identity already administers this site.");

    /// <summary>`402 Payment Required`, not a generic `409` - the backlog item's own reasoned choice.
    /// The actual remedy for a site at its seat limit is "upgrade", not "retry the same request later",
    /// which is exactly what `402` signals and `409` does not. The invite itself is never consumed by
    /// this rejection (`OperatorInviteRedemptionRepository`'s own remarks) - a later redemption of the
    /// identical code succeeds once a seat opens up.</summary>
    public static Error OperatorInviteSeatLimitReached(int seatLimit) =>
        new("OperatorInvite.SeatLimitReached", $"This site has reached its seat limit of {seatLimit}.");

    // `13-02`: same shared vocabulary, same reason - CreateCheckoutSessionHandler adds its own codes
    // here rather than a separate error class.
    /// <summary>The requested seat count falls outside <see cref="Domain.SubscriptionTierBands.MinSeats"/>-
    /// <see cref="Domain.SubscriptionTierBands.MaxSeats"/> - never a purchasable band, including the
    /// free tier's own single seat.</summary>
    public static Error BillingInvalidSeatCount(string reason) =>
        new("Billing.InvalidSeatCount", reason);

    /// <summary>ЮKassa answered but refused the payment-creation request (`CreatePaymentResult.Refused`) -
    /// a malformed request, bad credentials, or an amount ЮKassa's own validation rejected. Distinct
    /// from an unhandled transient failure (which this handler deliberately lets propagate as a `5xx` -
    /// see the handler's own remarks), because this is a terminal, provider-confirmed refusal a retry
    /// of the identical request would not fix.</summary>
    public static Error BillingPaymentProviderRefused(string reason) =>
        new("Billing.PaymentProviderRefused", reason);

    // `16-03`: same shared vocabulary, same reason - RequestSiteExportHandler/GetSiteExportStatusHandler
    // add their own codes here rather than a separate error class.
    public static Error ExportNotFound(Guid exportId) =>
        new("Export.NotFound", $"Export {exportId} was not found.");

    /// <summary>`13-06`: the identical "wrong site is indistinguishable from no such id" cross-tenant
    /// guard <see cref="ExportNotFound"/> already establishes, for a (retention class, period) key
    /// instead of a generated id - <see cref="Application.Abstractions.IMessageArchiveRepository.GetAsync"/>
    /// scopes by site, so an operator asking for another tenant's period gets this same code.</summary>
    public static Error MessageArchiveNotFound(string retentionClass, DateOnly periodStart) =>
        new("MessageArchive.NotFound", $"No archive for retention class '{retentionClass}', period {periodStart:yyyy-MM} was found.");

    /// <summary>Distinct code from <see cref="SiteRegistrationRateLimited"/> and the message send
    /// <c>RateLimited</c> above, the same reasoning each of those gives for its own distinct code - a
    /// client branching on `type` should be able to tell "you are exporting too often" apart from every
    /// other rate limit in this vocabulary without parsing the message text.</summary>
    public static Error ExportRateLimited(TimeSpan retryAfter) =>
        new("Export.RateLimited", $"Too many export requests - retry after {retryAfter.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");

    // `18-01`: same shared vocabulary, same reason - SearchConversationsHandler adds its own code here
    // rather than a separate error class.
    /// <summary>An empty search phrase, or a caller-supplied range whose start is not before its end -
    /// a client error either way. Mapped to `400` in `Ago.Chat.Api`'s `ErrorExtensions` (alongside
    /// `WidgetConfig.InvalidColor` and its siblings) rather than left to fall through to that switch's
    /// `500` default, since the query itself was malformed, not the server's handling of a well-formed
    /// one.</summary>
    public static Error SearchInvalidQuery(string reason) =>
        new("Conversation.SearchInvalidQuery", reason);

    // `13-03`: same shared vocabulary, same reason - CancelSubscriptionHandler/ChangeSubscriptionSeatsHandler
    // add their own codes here rather than a separate error class.
    public static Error BillingSubscriptionNotFound(Guid subscriptionId) =>
        new("Billing.SubscriptionNotFound", $"Billing subscription {subscriptionId} was not found for this site.");

    /// <summary>The subscription named is not currently active (<c>Pending</c>, already
    /// <c>Failed</c>/<c>Lapsed</c>) - there is nothing for a cancel or a seat change to act on.</summary>
    public static Error BillingSubscriptionNotActive(string reason) =>
        new("Billing.SubscriptionNotActive", reason);

    /// <summary>A requested seat count that resolves to the exact same tier band and seat count the
    /// subscription already charges for - neither an upgrade nor a downgrade, so there is nothing this
    /// endpoint's own asymmetric policy (`decisions/0006`) has anything to say about.</summary>
    public static Error BillingSeatCountUnchanged() =>
        new("Billing.SeatCountUnchanged", "The requested seat count matches the subscription's current seat count.");

    /// <summary>`13-03`: a site's `Permission.SiteManageOperators` holder tried to assign a seat beyond
    /// the site's own current `seat_limit` - `402 Payment Required`, the identical reasoning
    /// <see cref="OperatorInviteSeatLimitReached"/> already gives for the same underlying constraint on
    /// a different write path.</summary>
    public static Error OperatorSeatLimitReached(int seatLimit) =>
        new("Operator.SeatLimitReached", $"This site has reached its seat limit of {seatLimit}.");

    public static Error OperatorNotFound(Guid operatorId) =>
        new("Operator.NotFound", $"Operator {operatorId} was not found for this site.");

    public static Error OperatorAlreadyRemoved(Guid operatorId) =>
        new("Operator.AlreadyRemoved", $"Operator {operatorId} has already been removed.");

    // `18-02`: same shared vocabulary, same reason - TransferConversationHandler adds its own codes
    // here rather than a separate error class.
    /// <summary>
    /// The named target does not resolve to an operator who can actually receive this conversation -
    /// no such id on this site (<see cref="Domain.Permission"/>-scoped, so also the answer for a
    /// different site's operator, the same info-hiding "wrong tenant reads like no row" shape
    /// <c>AssignConversationHandler</c>'s own cross-tenant guard already uses), or a real row that
    /// currently <see cref="Domain.Operator.HoldsSeat"/> is <see langword="false"/> for or
    /// <see cref="Domain.Operator.RemovedAt"/> is set on. One code for all three: the caller's remedy
    /// is identical ("name a different operator") regardless of which is true, and distinguishing them
    /// on the wire would let a caller enumerate another tenant's roster by id, which
    /// <see cref="OperatorNotFound"/>'s own existing shape already refuses to do.
    /// </summary>
    public static Error TransferTargetNotEligible(Guid operatorId) =>
        new("Conversation.TransferTargetNotEligible", $"Operator {operatorId} cannot receive a transferred conversation.");

    /// <summary>The target named genuinely has no room - <see cref="Application.Abstractions.IOperatorCapacity.TryClaimAsync"/>
    /// lost the compare-and-set inside the transfer's own transaction. `402`-shaped in spirit but not
    /// in code: unlike <see cref="OperatorSeatLimitReached"/>/<see cref="OperatorInviteSeatLimitReached"/>,
    /// there is no purchase that fixes this - the remedy is "pick someone else, or wait", so this maps
    /// to a `409` the same way <see cref="ConcurrencyConflict"/> does, not to `402`.</summary>
    public static Error TransferTargetAtCapacity(Guid operatorId) =>
        new("Conversation.TransferTargetAtCapacity", $"Operator {operatorId} has no capacity for another conversation right now.");

    /// <summary>A transfer to the operator who already holds the conversation - not a state the domain
    /// needs to reject (<see cref="Domain.Conversation.TransferTo"/> would just re-assign the same id
    /// and raise a real event for nothing), but not a real request either. Checked before any
    /// permission or capacity work, the cheapest possible rejection.</summary>
    public static Error TransferTargetIsCurrentOperator() =>
        new("Conversation.TransferTargetIsCurrentOperator", "A conversation cannot be transferred to the operator who already holds it.");

    /// <summary>`18-02`'s own instance of `6-10`'s shape: the transfer's transaction lost a Postgres
    /// deadlock against the assignment engine (or another transfer) on every attempt this handler was
    /// willing to make. Unlike <c>CloseConversationHandler</c>'s own contention outcome, nothing here
    /// ever committed - there is no leaked slot to accept, only a request that must be retried, which
    /// is exactly what a `409` tells the caller to do.</summary>
    public static Error TransferContended(Guid conversationId) =>
        new("Conversation.TransferContended", $"Conversation {conversationId} could not be transferred because of write contention; retry the request.");

    // `18-04`: same shared vocabulary, same reason - the note/tag handlers add their own codes here
    // rather than a separate error class.
    /// <summary>An empty or oversized note body - <see cref="Domain.ConversationNote.Write"/>'s own
    /// invariant, translated at the Application boundary the same way <see cref="CannedResponseInvalid"/>
    /// translates <see cref="Domain.CannedResponse"/>'s.</summary>
    public static Error NoteInvalid(string reason) =>
        new("ConversationNote.Invalid", reason);

    public static Error TagNotFound(Guid tagId) =>
        new("Tag.NotFound", $"Tag {tagId} was not found for this site.");

    /// <summary>An empty or oversized tag name - <see cref="Domain.Tag"/>'s own invariant.</summary>
    public static Error TagInvalid(string reason) =>
        new("Tag.Invalid", reason);

    /// <summary>A tag name that already exists for this site (case-insensitive - `TagConfiguration`'s
    /// own unique index). `409`, not a silent rename of the existing row: a caller asking to create
    /// "Billing" when "billing" already exists almost certainly means to reuse it, and this makes that
    /// choice explicit rather than quietly creating a second label operators cannot tell apart.</summary>
    public static Error TagAlreadyExists(string name) =>
        new("Tag.AlreadyExists", $"A tag named '{name}' already exists for this site.");

    // `18-08`: same shared vocabulary, same reason - GetOperatorAnalyticsForSiteHandler adds its own
    // code here rather than a separate error class.
    /// <summary>The caller's own <c>from</c>/<c>to</c> is not a real half-open range - <c>from</c> not
    /// strictly before <c>to</c>. The same "the query itself was malformed" shape
    /// <see cref="SearchInvalidQuery"/> already gives for `18-01`'s own range check.</summary>
    public static Error AnalyticsInvalidRange(string reason) =>
        new("Analytics.InvalidRange", reason);

    // `18-10`: same shared vocabulary, same reason - SetConversationOutcomeHandler/
    // GetConversionReportForSiteHandler add their own codes here rather than a separate error class.
    /// <summary>The wire value did not parse to a real <see cref="Domain.ConversationOutcome"/> member,
    /// or parsed to <see cref="Domain.ConversationOutcome.Unset"/> - the same "validate the enum,
    /// translate the miss at the Application boundary" split <see cref="WidgetConfigInvalidLocale"/>
    /// already draws for <c>Locale</c>. <see cref="Domain.Conversation.SetOutcome"/>'s own
    /// <see cref="ArgumentOutOfRangeException"/> for <c>Unset</c> is a caller bug at that layer
    /// precisely because this check is meant to catch it first.</summary>
    public static Error OutcomeInvalid(string reason) =>
        new("Conversation.OutcomeInvalid", reason);

    // Note: the conversion report's own `from >= to` check reuses AnalyticsInvalidRange directly
    // (`GetConversionReportForSiteHandler`) rather than a second, near-identical code - unlike
    // `AttachmentDelete`-vs-`ConversationErase` and this file's other deliberately-split codes, there
    // is no client-observable difference between "this report's range was malformed" and "that report's
    // range was malformed"; both mean the same thing and want the same remedy, so one code serves both.

    // `18-14`: same shared vocabulary, same reason - GetModuleFlowReportForSiteHandler adds its own
    // code here rather than a separate error class. A distinct code from Analytics.InvalidRange above
    // rather than reuse (unlike `18-10`'s own conversion report right above, which does reuse it) -
    // `IModuleFlowReadStore` is a read-store over a genuinely different table (`module_tasks`, not
    // `conversations`), with its own materially different honesty caveat, so this report's own range
    // failure gets its own code rather than folding into a code named after a different report.
    /// <summary>The caller's own <c>from</c>/<c>to</c> is not a real half-open range - <c>from</c> not
    /// strictly before <c>to</c>. The same "the query itself was malformed" shape
    /// <see cref="AnalyticsInvalidRange"/> already gives for `18-08`'s own range check.</summary>
    public static Error ModuleFlowInvalidRange(string reason) =>
        new("ModuleFlow.InvalidRange", reason);

    // `20-07`: same shared vocabulary, same reason - EnableModuleForSiteHandler adds its own codes
    // here rather than a separate error class.
    public static Error ModuleInvalid(string reason) =>
        new("Module.Invalid", reason);

    /// <summary>`20-07`'s own trigger-conflict rule: a trigger word registered here already opens a
    /// *different* module enabled on this same site. The remedy is "pick a different trigger word", so
    /// the message names the word and the module it already belongs to - never silently first-match-wins
    /// at routing time, which is the failure this rejection exists to prevent at registration time
    /// instead.</summary>
    public static Error ModuleTriggerWordAlreadyRegistered(string word, string existingModuleKey) =>
        new("Module.TriggerWordAlreadyRegistered",
            $"Trigger word '{word}' is already registered to module '{existingModuleKey}' on this site.");

    /// <summary>`14-12`/`docs/conventions/text-commands.md`: a trigger word collides with Chat's own
    /// closed, product-level command vocabulary (<see cref="Domain.ReservedChatCommands"/>) - refused
    /// regardless of what any other module on this site has registered, since this word will never mean
    /// a module trigger on any site.</summary>
    public static Error ModuleTriggerWordReserved(string word) =>
        new("Module.TriggerWordReserved", $"Trigger word '{word}' is reserved for a built-in AGO Chat command.");

    // `19-01`: same shared vocabulary, same reason - GenerateReplyDraftHandler adds its own codes
    // here rather than a separate error class.
    /// <summary>Distinct code from every other <c>RateLimited</c> above, the same reasoning each of
    /// those gives for its own - a client branching on `type` should be able to tell "you asked for
    /// too many AI drafts" apart from a message-send or export rate limit without parsing the message
    /// text. `ReplyDraftRateLimitOptions`'s own remarks on why this cap exists and is checked before
    /// any provider call is made.</summary>
    public static Error ReplyDraftRateLimited(TimeSpan retryAfter) =>
        new("ReplyDraft.RateLimited", $"Too many reply-draft requests - retry after {retryAfter.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.");

    /// <summary>The provider was unreachable, timed out, or refused after every retry the resilience
    /// pipeline allows (`ResilientReplyDraftGenerator`'s own remarks) - `resilience.md`'s "no fallback
    /// content" rule applied honestly: there is nothing sensible to draft in its place, so this maps
    /// to `503` in `Ago.Chat.Api`'s `ErrorExtensions`, the same "this deployment, not the caller, is
    /// not ready" shape <c>ChannelNotAvailable</c> already uses.</summary>
    public static Error ReplyDraftUnavailable(string reason) =>
        new("ReplyDraft.Unavailable", reason);

    // `14-12`: same shared vocabulary, same reason - RequestChannelLinkFromConsoleHandler/
    // HandleLinkIdentityCommandHandler/UnlinkChannelIdentityHandler add their own codes here rather
    // than a separate error class.
    /// <summary>No <see cref="Domain.ChannelIdentity"/> matches the id named, or a real row that
    /// belongs to a different site - the same "wrong tenant reads like no row" info-hiding shape
    /// <see cref="OperatorNotFound"/> already establishes.</summary>
    public static Error ChannelIdentityNotFound(Guid channelIdentityId) =>
        new("ChannelIdentity.NotFound", $"Channel identity {channelIdentityId} was not found.");

    /// <summary>The wire value did not parse to a real <see cref="Domain.ChannelKind"/> member - the
    /// same "validate the enum, translate the miss at the Application boundary" split
    /// <see cref="WidgetConfigInvalidPosition"/> already draws for <c>Position</c>.</summary>
    public static Error ChannelLinkInvalidKind(string reason) =>
        new("ChannelLinkRequest.InvalidKind", $"'{reason}' is not a valid channel kind.");

    // `14-14`: same shared vocabulary, same reason - RecordVisitorContactDetailHandler/
    // DeleteVisitorContactDetailHandler add their own codes here rather than a separate error class.
    /// <summary>No <see cref="Domain.VisitorContactDetail"/> matches the id named, or a real row that
    /// belongs to a different visitor - the same "wrong tenant/visitor reads like no row" info-hiding
    /// shape <see cref="ChannelIdentityNotFound"/> already establishes.</summary>
    public static Error ContactDetailNotFound(Guid contactDetailId) =>
        new("VisitorContactDetail.NotFound", $"Contact detail {contactDetailId} was not found.");

    /// <summary>An empty or oversized value - <see cref="Domain.VisitorContactDetail.Record"/>'s own
    /// invariant, translated at the Application boundary the same way <see cref="NoteInvalid"/>
    /// translates <see cref="Domain.ConversationNote"/>'s.</summary>
    public static Error ContactDetailInvalid(string reason) =>
        new("VisitorContactDetail.Invalid", reason);

    /// <summary>The wire value did not parse to a real <see cref="Domain.VisitorContactDetailKind"/>
    /// member - the same "validate the enum, translate the miss at the Application boundary" split
    /// <see cref="ChannelLinkInvalidKind"/> already draws for <see cref="Domain.ChannelKind"/>.</summary>
    public static Error ContactDetailInvalidKind(string reason) =>
        new("VisitorContactDetail.InvalidKind", $"'{reason}' is not a valid contact detail kind.");
}
