using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Attachments;

/// <summary>
/// `5-03`: the backlog item's own recommendation - nested under a conversation for create (needs
/// conversation context for the participant/quota checks), standalone by attachment id for
/// confirm/download.
///
/// Every route here accepts *either* a visitor or an operator token
/// (<see cref="AuthorizationPolicies.EitherTokenKind"/> lists both <see cref="JwtSchemes"/>) - the
/// first endpoints in this codebase that do, since every hub before this was single-role by
/// construction (`VisitorHub` vs `OperatorHub`). ASP.NET Core tries each listed scheme and keeps
/// whichever one actually validates; because the two schemes' issuer/`aud`/signing requirements are
/// mutually exclusive, at most one ever succeeds for a real token, and
/// <see cref="ClaimsPrincipalExtensions.IsOperator"/> (backed by the `kind` claim) is how the handler
/// below tells which one without re-deriving it from claim shape.
///
/// `17-06`: "at most one", not "exactly one", is the correction that item's review produced - the
/// Operator scheme can validate a token that resolves to no operator at all, which is neither kind.
/// The policy now rejects that case up front; see <see cref="AuthorizationPolicies.EitherTokenKind"/>
/// for why it belongs there rather than in each handler's own branch.
/// </summary>
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        // `17-06`: a method group, not the inline lambda this used to be - `TokenSchemeSeparationTests`
        // configures the identical policy from the identical method, so the test proves *this* rule
        // rather than a transcription of it that is free to drift (`AuthEndpoints`'s own
        // "a named method, not an inline lambda" precedent, for the same reason).
        var group = app.MapGroup("/api/v1").RequireAuthorization(AuthorizationPolicies.EitherTokenKind);

        group.MapPost("/conversations/{conversationId:guid}/attachments", HandleCreateAsync);
        group.MapPost("/attachments/{attachmentId:guid}/confirm", HandleConfirmAsync);
        group.MapGet("/attachments/{attachmentId:guid}", HandleDownloadAsync);

        // `5-08`: operator-only, unlike every route above - a visitor never held `attachment:delete`
        // (see DeleteAttachmentAsOperator's own remarks). Mapped directly on `app`, not on `group` -
        // stacking a second RequireAuthorization policy on top of the group's own dual-scheme one
        // would combine (AND) both authentication requirements on the same endpoint rather than
        // replace it, which is not what a single-scheme route wants; ConversationsEndpoints'
        // operator-only `/conversations/queue` route makes the identical choice for the identical
        // reason (it is not nested under any shared group either).
        app.MapDelete("/api/v1/attachments/{attachmentId:guid}", HandleDeleteAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleCreateAsync(
        Guid conversationId,
        CreateAttachmentRequest request,
        CreateAttachmentHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var id = new ConversationId(conversationId);

        var result = user.IsOperator()
            ? await handler.HandleAsOperatorAsync(
                new CreateAttachmentAsOperator(
                    id, user.GetOperatorId(), user.GetSiteId(), request.ContentType, request.SizeBytes),
                cancellationToken)
            : await handler.HandleAsVisitorAsync(
                new CreateAttachmentAsVisitor(id, user.GetVisitorId(), request.ContentType, request.SizeBytes),
                cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/attachments/{result.Value.AttachmentId}",
            new CreateAttachmentResponse(result.Value.AttachmentId, result.Value.UploadUrl, result.Value.ExpiresAt));
    }

    private static async Task<IResult> HandleConfirmAsync(
        Guid attachmentId, ConfirmAttachmentHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var id = new AttachmentId(attachmentId);

        var result = user.IsOperator()
            ? await handler.HandleAsOperatorAsync(
                new ConfirmAttachmentAsOperator(id, user.GetOperatorId(), user.GetSiteId()), cancellationToken)
            : await handler.HandleAsVisitorAsync(new ConfirmAttachmentAsVisitor(id, user.GetVisitorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleDownloadAsync(
        Guid attachmentId, GetAttachmentDownloadUrlHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var id = new AttachmentId(attachmentId);

        var result = user.IsOperator()
            ? await handler.HandleAsOperatorAsync(
                new GetAttachmentDownloadUrlAsOperator(id, user.GetOperatorId(), user.GetSiteId()), cancellationToken)
            : await handler.HandleAsVisitorAsync(new GetAttachmentDownloadUrlAsVisitor(id, user.GetVisitorId()), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(new AttachmentDownloadResponse(
            result.Value.Url, result.Value.ContentType, result.Value.ThumbnailUrl, result.Value.ExpiresAt));
    }

    private static async Task<IResult> HandleDeleteAsync(
        Guid attachmentId, DeleteAttachmentHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var id = new AttachmentId(attachmentId);

        var result = await handler.HandleAsOperatorAsync(
            new DeleteAttachmentAsOperator(id, user.GetOperatorId(), user.GetSiteId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    public sealed record CreateAttachmentRequest(string ContentType, long SizeBytes);

    public sealed record CreateAttachmentResponse(Guid AttachmentId, Uri UploadUrl, DateTimeOffset ExpiresAt);

    public sealed record AttachmentDownloadResponse(Uri Url, string ContentType, Uri? ThumbnailUrl, DateTimeOffset ExpiresAt);
}
