using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
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
    }

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
}
