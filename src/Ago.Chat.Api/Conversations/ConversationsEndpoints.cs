using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Conversations;

/// <summary>
/// `5-07`: `GET /api/v1/conversations/queue` - api-design.md's "actions that are not CRUD become
/// sub-resources" shape, applied to a compound read rather than a write: an operator's queue view is
/// not a filtered list of the plural `conversations` resource so much as a standing question ("what's
/// waiting, what's mine") that always wants both halves in one round trip
/// (`OperatorQueueResponse`'s own remarks), so a sub-resource reads better than
/// `?status=waiting&amp;assignedTo=me` query parameters would.
///
/// Operator-only (unlike `AttachmentEndpoints`, which accepts both schemes) - a visitor has no queue
/// to view, so there is no dual-scheme ambiguity to resolve here the way `ClaimsPrincipalExtensions.
/// IsOperator` resolves it there. Reuses the same `"RequireOperatorIdentity"` policy `Program.cs`
/// already declares and `OperatorHub` already applies (scheme + the `OperatorId` claim
/// `OperatorIdentityClaimsTransformation` adds) rather than redeclaring the requirement inline.
/// </summary>
public static class ConversationsEndpoints
{
    public static void MapConversationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/conversations/queue", HandleGetQueueAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `5-08`: the admin/supervisor site-wide list - a sibling sub-resource to `/queue`, same
        // "compound read gets its own sub-resource rather than a query-parameter mode switch on the
        // plural `conversations` resource" reasoning as this file's own doc comment already gives for
        // `/queue`. `beforeId`/`pageSize` are query parameters, not a route segment, because they page
        // one already-identified resource rather than select which resource this is (api-design.md).
        app.MapGet("/api/v1/conversations/all", HandleGetAllForSiteAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `6-02`: api-design.md's "actions that are not CRUD become sub-resources" example, verbatim -
        // operator-only like `/queue` and `/all` above, for the identical reason (a visitor closing
        // their own conversation is a different action - ending a chat session client-side - not this
        // one; see CloseConversationHandler's own remarks).
        app.MapPost("/api/v1/conversations/{conversationId:guid}/close", HandleCloseAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `5-15`: the same sub-resource shape as `/close` right above it. REST rather than a method on
        // `OperatorHub` - see this file's `HandleMarkReadAsync` for the argument, which is not the
        // obvious one.
        app.MapPost("/api/v1/conversations/{conversationId:guid}/read", HandleMarkReadAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    /// <summary>The body carries a sequence rather than the route saying "clear it" - see
    /// <c>MarkConversationRead</c>. Kept as a body, not a query parameter, because it is the state
    /// being asserted ("my read position is N"), not a modifier on which resource is addressed
    /// (api-design.md).</summary>
    public sealed record MarkConversationReadRequest(int UpToSequence);

    private static async Task<IResult> HandleGetQueueAsync(
        GetOperatorQueueHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetOperatorQueue(user.GetOperatorId(), user.GetSiteId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleGetAllForSiteAsync(
        Guid? beforeId,
        int? pageSize,
        GetAllConversationsForSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetAllConversationsForSite(user.GetOperatorId(), user.GetSiteId(), beforeId, pageSize ?? 50),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleCloseAsync(
        Guid conversationId, CloseConversationHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new CloseConversation(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    /// <summary>
    /// `5-15`. <b>Why REST and not a hub method</b>, which was the likelier-looking option: the
    /// deciding argument is that this write's failure modes have to be *visible*. A non-assigned
    /// operator trying to clear someone else's count is a real `403` with an RFC 7807 body here
    /// (api-design.md's own error convention, `ErrorExtensions`); over SignalR it would be a
    /// `HubException` carrying a string, indistinguishable at the client from a transport fault. The
    /// same goes for the `409` a doubly-raced write returns. The usual argument for the hub - "the
    /// console already holds the connection, and this is a high-frequency low-value write" - turns out
    /// not to hold under `5-15`'s chosen semantics: mark-read fires once per conversation *open* plus
    /// a debounced call while one is on screen, which is a handful of requests a minute per operator,
    /// not per-message traffic. And unlike a hub invocation, an HTTP call does not silently vanish
    /// while the hub is mid-reconnect, which is exactly when an operator is most likely to be catching
    /// up on a backlog. If this ever does become hot, moving it to the hub is a transport change with
    /// no handler change - the use case does not know which one called it.
    /// </summary>
    private static async Task<IResult> HandleMarkReadAsync(
        Guid conversationId,
        MarkConversationReadRequest request,
        MarkConversationReadHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new MarkConversationRead(
                new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId(), request.UpToSequence),
            cancellationToken);

        // 200 with the resulting count, not 204 like `/close` - the console's badge is the whole point
        // of the call, and the server's own answer saves it a queue refetch to find out what the number
        // became. It also makes the no-op case honest: an already-read conversation returns the count
        // as it actually stands rather than an assumed zero.
        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }
}
