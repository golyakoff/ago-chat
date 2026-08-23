using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
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
/// (<c>RequireAuthorization</c> below lists both <see cref="JwtSchemes"/>) - the first endpoints in
/// this codebase that do, since every hub before this was single-role by construction (`VisitorHub`
/// vs `OperatorHub`). ASP.NET Core tries each listed scheme and keeps whichever one actually
/// validates; because the two schemes' `aud` requirements are mutually exclusive, exactly one ever
/// succeeds for a real token, and <see cref="ClaimsPrincipalExtensions.IsOperator"/> (backed by the
/// `kind` claim <see cref="JwtTokenService"/> now issues) is how the handler below tells which one
/// without re-deriving it from claim shape.
/// </summary>
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Visitor, JwtSchemes.Operator)
                .RequireAuthenticatedUser());

        group.MapPost("/conversations/{conversationId:guid}/attachments", HandleCreateAsync);
        group.MapPost("/attachments/{attachmentId:guid}/confirm", HandleConfirmAsync);
        group.MapGet("/attachments/{attachmentId:guid}", HandleDownloadAsync);
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

    public sealed record CreateAttachmentRequest(string ContentType, long SizeBytes);

    public sealed record CreateAttachmentResponse(Guid AttachmentId, Uri UploadUrl, DateTimeOffset ExpiresAt);

    public sealed record AttachmentDownloadResponse(Uri Url, string ContentType, Uri? ThumbnailUrl, DateTimeOffset ExpiresAt);
}
