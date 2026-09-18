using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Api.Conversations;

/// <summary>
/// `25-143`: `GET /api/v1/conversations/{conversationId}/unread-count` - the widget's own reload-time
/// seed for its closed-launcher badge (`25-141`). A visitor who kept a tab closed while an operator
/// replied has no live hub connection yet on the next load, and by design (`adr/0148`'s lazy-connect
/// posture) none is opened just to answer this - this is a plain HTTP read, reachable with nothing
/// more than the visitor token the widget already holds in storage.
///
/// <para><b>Own <c>Map</c> call, not folded into <see cref="ConversationsEndpoints.MapConversationsEndpoints"/>.</b>
/// That file's own doc comment states it is operator-only end to end ("unlike `AttachmentEndpoints`,
/// which accepts both schemes... a visitor has no queue to view"); this route is the opposite -
/// visitor-only - so mapping it there would falsify that file's own stated invariant the moment a test
/// host mapped only <c>MapConversationsEndpoints</c> and, on the strength of that comment, assumed
/// every route it exposes requires an operator identity. Same "a route with a materially different
/// auth shape gets its own Map call" precedent <c>VisitorRestrictionsEndpoints</c>'s own remarks give
/// for itself, applied to the opposite direction (visitor-only beside an operator-only file, rather
/// than operator-only beside an operator-only file).</para>
///
/// <para><b><see cref="JwtSchemes.Visitor"/> directly, not the dual-scheme
/// <see cref="AuthorizationPolicies.EitherTokenKind"/> <c>PhoneVerificationEndpoints</c> narrows
/// inline.</b> That file takes the shared policy because its route sits on a path an operator caller
/// could in principle reach and must be rejected from, structurally, rather than silently
/// misclassified (its own remarks). No operator caller makes sense on this route at all - an
/// operator's own unread count is <c>ConversationSummaryDto.OperatorUnreadCount</c>, a different
/// number entirely, read a different way - so there is no third state here to reject; the single-scheme
/// shape <c>AuthEndpoints.HandleVisitorSessionRenewalAsync</c>'s own mapping already uses for the
/// identical reason is the fit.</para>
/// </summary>
public static class UnreadCountEndpoints
{
    public static void MapUnreadCountEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/conversations/{conversationId:guid}/unread-count", HandleGetUnreadCountAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtSchemes.Visitor });
    }

    /// <summary>The wire shape of a successful read - one field, matching this route's own single
    /// purpose (unlike <see cref="ConversationsEndpoints.HandleMarkReadAsync"/>'s richer response,
    /// there is no watermark to hand back here: the widget already knows the sequence it asked
    /// about).</summary>
    public sealed record UnreadCountResponse(int Count);

    // Public, not private - ConversationsEndpoints.HandleInitiateAsync's own precedent: a test can call
    // this directly with hand-built dependencies, no hosting pipeline needed.
    public static async Task<IResult> HandleGetUnreadCountAsync(
        Guid conversationId,
        int? afterSequence,
        GetConversationHistoryHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;

        // `afterSequence` absent means "this visitor has never had a read position" - the widget's own
        // contract (this item's Scope: "omit it entirely for a visitor who has never had one"). Zero is
        // the correct default rather than a special case: GetUnreadCountAsync's own `sequence > 0` reads
        // as "every message ever sent" for a conversation whose lowest real sequence is 1, the identical
        // "zero means nothing read yet" convention Conversation.OperatorLastReadSequence's own remarks
        // already state for the operator side of this same table.
        var result = await handler.HandleUnreadCountAsVisitorAsync(
            new GetUnreadCountAsVisitor(new ConversationId(conversationId), user.GetVisitorId(), afterSequence ?? 0),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(new UnreadCountResponse(result.Value));
    }
}
