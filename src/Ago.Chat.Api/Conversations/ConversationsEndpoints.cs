using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetOperatorQueue;

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
    }

    private static async Task<IResult> HandleGetQueueAsync(
        GetOperatorQueueHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetOperatorQueue(user.GetOperatorId(), user.GetSiteId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }
}
