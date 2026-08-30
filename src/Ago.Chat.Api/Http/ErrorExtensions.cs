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
                or "Tag.NotFound" => StatusCodes.Status404NotFound,
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
                or "Conversation.OutcomeInvalid" => StatusCodes.Status400BadRequest,
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
                or "Tag.AlreadyExists" => StatusCodes.Status409Conflict,
            // `13-01`'s own reasoned choice: a real invite that has timed out is "Gone", not "Not
            // Found" - a caller should ask for a fresh one, not retry the same lookup more carefully.
            "OperatorInvite.Expired" => StatusCodes.Status410Gone,
            // `13-01`'s own reasoned choice: `402 Payment Required`, not a generic `409` - the actual
            // remedy for a site at its seat limit is "upgrade", not "retry", which `402` signals
            // honestly and `409` does not (ConversationErrors.OperatorInviteSeatLimitReached's own
            // remarks).
            "OperatorInvite.SeatLimitReached" => StatusCodes.Status402PaymentRequired,
            // `19-01`: its own distinct rate-limit code, same 429 group - ConversationErrors.ReplyDraftRateLimited's
            // own remarks.
            "Message.RateLimited" or "Site.RateLimited" or "Export.RateLimited" or "ReplyDraft.RateLimited"
                => StatusCodes.Status429TooManyRequests,
            // `14-08`: this deployment, not the caller, is not ready - ConversationErrors.ChannelNotAvailable's
            // own remarks. `19-01`: ReplyDraft.Unavailable is the identical shape - the LLM provider is
            // unreachable, not anything the caller did wrong.
            "ChannelCredential.NotAvailable" or "ReplyDraft.Unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        // No Retry-After header: Error carries no structured retry-after value for a 429
        // (ConversationErrors.RateLimited's own remarks - it rides in the message text, and every
        // existing caller, VisitorHub's HubException included, already just forwards that text
        // verbatim rather than parsing it back out for a header).
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
