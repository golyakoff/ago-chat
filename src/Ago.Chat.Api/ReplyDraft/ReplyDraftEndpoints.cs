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

    // `ago-root#353`: public, not private - `AttachmentEndpoints.HandleCreateAsync`'s own reasoning:
    // a test can call this directly to prove the Retry-After header, no hosting pipeline needed.
    public static async Task<IResult> HandleGenerateAsync(
        Guid conversationId,
        GenerateReplyDraftHandler handler,
        ReplyDraftRateLimitOptions rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;

        var result = await handler.HandleAsync(
            new GenerateReplyDraftAsOperator(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `ago-root#353`: the operator and site buckets `GenerateReplyDraftHandler` checks share
            // this one code - the slower of the two is the safe conservative answer either way.
            var retryAfter = error.Code == "ReplyDraft.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerOperatorRefillPerSecond, rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        return Results.Ok(new ReplyDraftResponse(result.Value.DraftText));
    }

    public sealed record ReplyDraftResponse(string DraftText);
}
