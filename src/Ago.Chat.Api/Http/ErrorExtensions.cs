using System.Globalization;
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
    /// <summary>
    /// <paramref name="retryAfter"/>: `ago-root#353`. Optional and caller-supplied, never derived in
    /// here - this method has no way to tell a rate-limited <see cref="Error"/> apart from any other
    /// (the vocabulary is <c>(Code, Message)</c>, nothing more), so the five endpoints that own a
    /// <c>*.RateLimited</c> code compute their own conservative wait via <see cref="RateLimitRetryAfter.Conservative"/>,
    /// from configuration they already hold, and pass it in only for that one code. Every other call
    /// site keeps calling <c>error.ToProblem(httpContext)</c> unchanged.
    /// </summary>
    public static IResult ToProblem(this Error error, HttpContext httpContext, TimeSpan? retryAfter = null)
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
                or "PhoneVerification.NotFound"
                // `22-11`: an operator tried to rotate/revoke/check a module registration for a site
                // that does not have that module enabled - the same "nothing to act on" shape every
                // other NotFound code in this group already gets.
                or "Module.NotEnabled" => StatusCodes.Status404NotFound,
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
                or "PhoneVerification.InvalidNumber" or "PhoneVerification.WrongCode"
                // `22-17`: the owner's own mistake to fix - an ExpiresAt that is not strictly in the
                // future, or reaches further out than EnableModuleForSiteAsOwnerHandler.MaxGrantDuration
                // allows.
                or "Module.GrantExpiryInvalid" => StatusCodes.Status400BadRequest,
            "Conversation.InvalidState" or "Attachment.VerificationFailed" or "Attachment.NotReady"
                or "Conversation.ConcurrencyConflict" or "Site.AlreadyRegistered"
                or "ChannelCredential.AlreadyConnected" or "OperatorInvite.AlreadyRedeemed"
                or "OperatorInvite.AlreadyOperatorOnSite"
                // `18-02`: both genuinely 409-shaped - "retry the request", not "fix what you sent".
                // TransferTargetAtCapacity is not 402 like OperatorInviteSeatLimitReached: there is no
                // purchase that adds room to one specific operator right now (ConversationErrors'
                // own remarks on why).
                or "Conversation.TransferTargetAtCapacity" or "Conversation.TransferContended"
                // `23-04`: the identical "retry the request" shape as Conversation.TransferContended,
                // for a deliberate take's own transaction losing every attempt against write
                // contention - ConversationErrors.ClaimContended's own remarks.
                or "Conversation.ClaimContended"
                // `18-04`: a real conflict with existing data (a duplicate name), not a malformed
                // request - ConversationErrors.TagAlreadyExists's own remarks.
                or "Tag.AlreadyExists"
                // `14-15`: a genuine race between two concurrent confirmations of the same pending
                // verification - ConversationErrors.PhoneVerificationAlreadyConsumed's own remarks.
                or "PhoneVerification.AlreadyConsumed"
                // `14-15`/`adr/0079` decision 3: the identical "refused, not merged" conflict for a
                // phone number already verified under a different visitor - ConversationErrors.
                // PhoneVerificationAlreadyLinkedToAnotherVisitor's own remarks.
                or "PhoneVerification.AlreadyLinkedToAnotherVisitor"
                // `23-26`: a real conflict with the site's own current state (removing this operator
                // would leave nobody who can manage operators), not a malformed request - the same
                // "retry makes no sense, the remedy is a different action first" shape every other code
                // in this group already gives for its own conflict.
                or "Operator.IsLastManager" => StatusCodes.Status409Conflict,
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
            // 500` default below. DemoEndpoints.HandleMintAsync adds its own Retry-After header before
            // reaching this method, by reading the number back out of the error's own message rather
            // than widening Error itself (Ago.Platform.Kernel is out of scope for an ago-chat-only fix)
            // - DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds's own remarks.
            // `ago-root#353`: the five codes right below - Message/Site/Export/ReplyDraft/PhoneVerification.RateLimited
            // - carried no Retry-After at all until this item, this method's own former comment said so
            // in as many words. They do not reuse demo.rate_limited's message round trip (closed for
            // five more call sites, ago-root#353's own item file) - each endpoint computes a conservative
            // wait from configuration it already holds (RateLimitRetryAfter.Conservative) and passes it
            // into this method's own `retryAfter` parameter below, which is where the header is actually
            // set. PhoneVerification.LockedOut still carries none, deliberately - see its own remarks two
            // lines up.
            // `24-11`: PersonExport.RateLimited joins the same group - ConversationErrors.PersonExportRateLimited's
            // own remarks on why it is a distinct code from Export.RateLimited rather than a reuse.
            "Message.RateLimited" or "Site.RateLimited" or "Export.RateLimited" or "PersonExport.RateLimited"
                or "ReplyDraft.RateLimited"
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
                or "demo.identity_rejected"
                // `24-03`: the identical "a dependency of this request is missing, not anything the
                // caller supplied being wrong" shape - a required document was declared but never
                // published yet (ConversationErrors.SiteAgreementUnavailable's own remarks).
                or "Site.AgreementUnavailable"
                // `22-11`: the module deployment refused the provisioning call or could not be
                // reached - a dependency of this request failing, not anything the caller supplied
                // being wrong, the identical reasoning ChannelCredential.NotAvailable/ReplyDraft.Unavailable's
                // own comment gives for its group.
                or "Module.RegistrationFailed" => StatusCodes.Status503ServiceUnavailable,
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

        // `ago-root#353`: the one place every *.RateLimited code's Retry-After header gets set, driven
        // by data the caller attached rather than five near-identical blocks at each call site
        // (AttachmentEndpoints, SitesEndpoints twice, ReplyDraftEndpoints, PhoneVerificationEndpoints).
        // demo.rate_limited (`ago-root#347`) still sets its own header before reaching here, via the
        // message round trip DemoTenantErrors.TryGetRateLimitedRetryAfterSeconds documents - that one
        // predates this item and is out of its scope to touch, so DemoEndpoints keeps its own path.
        //
        // RFC 9110 SS10.2.3: delta-seconds, a non-negative integer. Ceiling, and never below 1 -
        // VisitorSessionRenewalTests treats a `0` as a bug, not a fast retry (ago-widget hammers the
        // endpoint immediately on `0` rather than backing off), so a sub-second wait must still read
        // as "wait a second," not "retry now."
        if (retryAfter is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

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
