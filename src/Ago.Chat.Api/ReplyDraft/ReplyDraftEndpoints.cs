using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GenerateReplyDraft;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ReplyDraft;

/// <summary>
/// `19-01`: operator-only, mapped directly on <c>app</c> rather than under any dual-scheme group -
/// `AttachmentEndpoints.HandleDeleteAsync`'s own precedent for the identical reason (stacking a second
/// `RequireAuthorization` policy on top of a group's own dual-scheme one would combine, not replace,
/// the authentication requirement). There is no visitor entry point to share a group with in the first
/// place - <see cref="GenerateReplyDraftAsOperator"/>'s own remarks on why this feature has exactly one
/// caller kind.
/// </summary>
public static class ReplyDraftEndpoints
{
    public static void MapReplyDraftEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/conversations/{conversationId:guid}/reply-draft", HandleGenerateAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleGenerateAsync(
        Guid conversationId,
        GenerateReplyDraftHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;

        var result = await handler.HandleAsync(
            new GenerateReplyDraftAsOperator(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ReplyDraftResponse(result.Value.DraftText));
    }

    public sealed record ReplyDraftResponse(string DraftText);
}
