using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Http;

/// <summary>
/// `api-design.md`: "Errors are RFC 7807 problem details with a stable machine-readable `type`...
/// clients branch on `type`, never on the message." <see cref="Ago.Chat.Application.UseCases.ConversationErrors"/>'s
/// codes already are that stable vocabulary (shared with every hub's `HubException` today) - this is
/// the first HTTP endpoint file to translate one into a response, so the mapping lives here rather
/// than beside `AuthEndpoints`, which never went through `Result&lt;T&gt;`/`Error` at all (`3-05`'s
/// rate limit and site lookup built their `Results.Problem` calls by hand, before this vocabulary had
/// an HTTP-facing consumer).
/// </summary>
public static class ErrorExtensions
{
    public static IResult ToProblem(this Error error, HttpContext httpContext)
    {
        var statusCode = error.Code switch
        {
            "Conversation.NotFound" or "Attachment.NotFound" or "WebhookEndpoint.NotFound" or "Site.NotFound"
                or "ChannelCredential.NotFound" or "OperatorInvite.NotFound" or "Export.NotFound"
                // `18-02`: deliberately the same 404 group as Conversation.NotFound, not its own
                // bucket - ConversationErrors.TransferTargetNotEligible's own remarks on why a
                // wrong-tenant or ineligible operator must read exactly like one that does not exist.
                or "Conversation.TransferTargetNotEligible"
                // `18-04`: same "wrong tenant reads like no such row" info-hiding shape - a tag id
                // from a different site is indistinguishable from one that never existed.
                or "Tag.NotFound"
                // `14-12`: the identical "wrong tenant reads like no such row" shape - a channel
                // identity id from a different site is indistinguishable from one that never existed.
                or "ChannelIdentity.NotFound"
                // `14-14`: the identical "wrong visitor reads like no such row" shape - a contact
                // detail id belonging to a different visitor is indistinguishable from one that never
                // existed (DeleteVisitorContactDetailHandler's own remarks).
                or "VisitorContactDetail.NotFound"
                // `14-13`: the same info-hiding shape once more - naming a real id that exists but is
                // not eligible (someone else's identity, or unlinked) must read exactly like naming one
                // that never existed at all, ConversationErrors.ChannelIdentityNotEligibleForPreference's
                // own remarks.
                or "ChannelIdentity.NotEligibleForPreference"
                // `14-15`: the identical "wrong tenant/visitor reads like no such row" shape once more -
                // a pending phone verification id from a different site or a different visitor is
                // indistinguishable from one that never existed.
                or "PhoneVerification.NotFound" => StatusCodes.Status404NotFound,
            "Conversation.Forbidden" => StatusCodes.Status403Forbidden,
            "Attachment.TooLarge" => StatusCodes.Status413PayloadTooLarge,
            "Attachment.InvalidContentType" or "WebhookEndpoint.InvalidUrl"
                or "WidgetConfig.InvalidColor" or "WidgetConfig.InvalidPosition"
                or "Site.InvalidName" or "Site.InvalidOrigin" or "ChannelCredential.InvalidToken"
                or "OperatorInvite.InvalidRole" or "Conversation.SearchInvalidQuery"
                // `18-02`: a real client mistake (naming the operator who already holds the
                // conversation), not a conflict with anything concurrent - see the error's own remarks.
                or "Conversation.TransferTargetIsCurrentOperator"
                // `18-03`: CannedResponseEndpoints' own PUT - an empty/oversized title or body, or too
                // many responses, is the caller's mistake to fix, not a server failure.
                or "CannedResponse.Invalid"
                // `18-04`: the same "caller's mistake to fix" shape - an empty/oversized note body or
                // tag name.
                or "ConversationNote.Invalid" or "Tag.Invalid"
                // `18-08`: the same "the query itself was malformed" shape as
                // Conversation.SearchInvalidQuery, for the analytics panel's own from/to - `18-10`'s
                // conversion report reuses this exact code for its own identical check
                // (ConversationErrors.AnalyticsInvalidRange's own remarks on why it does not get a
                // second one).
                or "Analytics.InvalidRange"
                // `18-10`: a wire value that did not parse to a real, recordable ConversationOutcome -
                // the caller's own mistake to fix, the same shape as the CannedResponse/Tag/Note
                // validation codes right above it.
                or "Conversation.OutcomeInvalid"
                // `18-14`: the same "the query itself was malformed" shape again, for the
                // module-flow report's own from/to (ConversationErrors.ModuleFlowInvalidRange's own
                // remarks on why it is a distinct code from Analytics.InvalidRange rather than a
                // third reuse).
                or "ModuleFlow.InvalidRange"
                // `14-12`: the same "the query itself was malformed" shape - a channel-kind string that
                // does not parse to a real Domain.ChannelKind member.
                or "ChannelLinkRequest.InvalidKind"
                // `14-14`: the same "caller's mistake to fix" shape as ConversationNote.Invalid/
                // Tag.Invalid above - an empty/oversized contact detail value, or a kind string that
                // does not parse to a real Domain.VisitorContactDetailKind member.
                or "VisitorContactDetail.Invalid" or "VisitorContactDetail.InvalidKind"
                // `14-15`: the caller's own mistake to fix - an unparsable phone number, or a code that
                // did not match (ConversationErrors.PhoneVerificationWrongCode's own remarks on why the
                // message never names a remaining-attempts count).
                or "PhoneVerification.InvalidNumber" or "PhoneVerification.WrongCode" => StatusCodes.Status400BadRequest,
            "Conversation.InvalidState" or "Attachment.VerificationFailed" or "Attachment.NotReady"
                or "Conversation.ConcurrencyConflict" or "Site.AlreadyRegistered"
                or "ChannelCredential.AlreadyConnected" or "OperatorInvite.AlreadyRedeemed"
                or "OperatorInvite.AlreadyOperatorOnSite"
                // `18-02`: both genuinely 409-shaped - "retry the request", not "fix what you sent".
                // TransferTargetAtCapacity is not 402 like OperatorInviteSeatLimitReached: there is no
                // purchase that adds room to one specific operator right now (ConversationErrors'
                // own remarks on why).
                or "Conversation.TransferTargetAtCapacity" or "Conversation.TransferContended"
                // `18-04`: a real conflict with existing data (a duplicate name), not a malformed
                // request - ConversationErrors.TagAlreadyExists's own remarks.
                or "Tag.AlreadyExists"
                // `14-15`: a genuine race between two concurrent confirmations of the same pending
                // verification - ConversationErrors.PhoneVerificationAlreadyConsumed's own remarks.
                or "PhoneVerification.AlreadyConsumed"
                // `14-15`/`adr/0079` decision 3: the identical "refused, not merged" conflict for a
                // phone number already verified under a different visitor - ConversationErrors.
                // PhoneVerificationAlreadyLinkedToAnotherVisitor's own remarks.
                or "PhoneVerification.AlreadyLinkedToAnotherVisitor" => StatusCodes.Status409Conflict,
            // `13-01`'s own reasoned choice: a real invite that has timed out is "Gone", not "Not
            // Found" - a caller should ask for a fresh one, not retry the same lookup more carefully.
            // `14-15`: the identical shape for an expired verification code - ConversationErrors.
            // PhoneVerificationExpired's own remarks.
            "OperatorInvite.Expired" or "PhoneVerification.Expired" => StatusCodes.Status410Gone,
            // `13-01`'s own reasoned choice: `402 Payment Required`, not a generic `409` - the actual
            // remedy for a site at its seat limit is "upgrade", not "retry", which `402` signals
            // honestly and `409` does not (ConversationErrors.OperatorInviteSeatLimitReached's own
            // remarks).
            "OperatorInvite.SeatLimitReached" => StatusCodes.Status402PaymentRequired,
            // `ago-root#352`: a deployment that has not turned demo tenants on genuinely lacks this
            // capability - not "there is nothing at this path" (`404`, explicitly rejected by
            // MintDemoTenantHandler's own remarks: "not a 404 dressed as a feature flag") and not "an
            // upstream dependency is failing right now" (`503`, the group below, where demo.capacity_reached
            // and demo.identity_rejected land). `501 Not Implemented` is the status RFC 7231 reserves for
            // exactly this shape - "the server does not support the functionality required to fulfil the
            // request" - and it is a static property of this deployment's own configuration, not a
            // transient condition any `Retry-After` could ever shorten.
            "demo.disabled" => StatusCodes.Status501NotImplemented,
            // `19-01`: its own distinct rate-limit code, same 429 group - ConversationErrors.ReplyDraftRateLimited's
            // own remarks.
            // `14-15`: its own distinct rate-limit code, same 429 group - ConversationErrors.
            // PhoneVerificationRateLimited's own remarks. LockedOut shares the group for the identical
            // "a fresh attempt, not a permission, is the remedy" reasoning
            // (ConversationErrors.PhoneVerificationLockedOut's own remarks), even though it carries no
            // Retry-After.
            // `ago-root#347`: `demo.rate_limited` belongs in this same group and was simply missing from
            // it - every other rate-limit code above was already mapped correctly, so exceeding the
            // per-IP demo mint limit was the one refusal in this switch that fell through to the `_ =>
            // 500` default below. DemoEndpoints.HandleMintAsync adds the Retry-After header this group's
            // own comment says Error cannot carry, for this one code only, by reading the number back out
            // of the error's own message rather than widening Error itself (Ago.Platform.Kernel is out of
            // scope for an ago-chat-only fix) - DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds's own
            // remarks.
            "Message.RateLimited" or "Site.RateLimited" or "Export.RateLimited" or "ReplyDraft.RateLimited"
                or "PhoneVerification.RateLimited" or "PhoneVerification.LockedOut" or "demo.rate_limited"
                => StatusCodes.Status429TooManyRequests,
            // `14-08`: this deployment, not the caller, is not ready - ConversationErrors.ChannelNotAvailable's
            // own remarks. `19-01`: ReplyDraft.Unavailable is the identical shape - the LLM provider is
            // unreachable, not anything the caller did wrong.
            // `ago-root#352`: demo.capacity_reached joins this group for a related but distinct reason -
            // not "the deployment isn't ready" but "the deployment is at a real ceiling that does not
            // refill on a clock the way a rate limit does" (DemoTenantErrors.CapacityReached's own
            // remarks: "each one expires on its own"), so `429`'s implied "the same request works again
            // shortly, on a schedule" is the wrong promise. `503`'s "the server cannot handle this right
            // now" is the honest one - a live tenant expiring is what frees the room, not the passage of
            // a fixed window.
            // `ago-root#352`: demo.identity_rejected joins for the same "deployment-side dependency, not
            // caller-side mistake" reasoning as ChannelCredential.NotAvailable/ReplyDraft.Unavailable above
            // - KeycloakDemoIdentityProvisioner.CreateAsync returns it whenever Keycloak itself refuses or
            // answers unexpectedly (a `409`/`400` from a randomly generated username colliding, or a `201`
            // with no `Location` header), never because of anything the anonymous demo caller supplied.
            // Not `409`: the caller never named an identifier of their own to conflict with, so there is
            // nothing for *them* to change before retrying - a fresh mint attempt generates a brand new
            // random username server-side, which is exactly `503`'s "try again" and not `409`'s "you
            // conflicted with a specific resource, send a different one".
            "ChannelCredential.NotAvailable" or "ReplyDraft.Unavailable" or "demo.capacity_reached"
                or "demo.identity_rejected" => StatusCodes.Status503ServiceUnavailable,
            // `ago-root#352`: demo.unavailable is deliberately left here rather than given its own status.
            // MintDemoTenantHandler returns it only after ISiteRegistrationRepository.TryRegisterAsync's
            // five-row insert hits its own unique-index violation - a race that port's own remarks call
            // "effectively unreachable in ordinary operation" now that every mint generates a fresh
            // siteId. Unlike its three siblings above, this is not a deliberate business refusal wearing
            // the wrong number; it is an unexpected database conflict reached after a Keycloak identity
            // was already created and then compensated away - the "possibly genuinely 500" case the ticket
            // itself flagged, and 500 is the honest status for "something we did not expect happened while
            // trying to do this."
            "demo.unavailable" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError,
        };

        // No Retry-After header here: Error carries no structured retry-after value for a 429
        // (ConversationErrors.RateLimited's own remarks - it rides in the message text, and every
        // existing caller, VisitorHub's HubException included, already just forwards that text
        // verbatim rather than parsing it back out for a header). This is a real, wider gap - every
        // *.RateLimited code in the switch above except demo.rate_limited shares it - but `ago-root#347`
        // only asked for the demo endpoint, so only DemoEndpoints.HandleMintAsync adds the header, before
        // calling this generic mapper, rather than this method growing a special case for one caller.
        return Results.Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode,
            // A bare slug, not a resolvable URL - matches AuthEndpoints' own `type` values
            // ("rate-limited", "site-not-found"), not a dereferenceable RFC 7807 type URI.
            type: error.Code,
            extensions: new Dictionary<string, object?> { ["traceId"] = httpContext.TraceIdentifier });
    }
}
