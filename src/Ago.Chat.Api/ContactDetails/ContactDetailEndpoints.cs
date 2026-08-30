using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ContactDetails;

/// <summary>
/// `14-14`/`adr/0079` section 6: `GET`/`POST /api/v1/conversations/{conversationId}/contact-details`
/// and `DELETE /api/v1/conversations/{conversationId}/contact-details/{id}` - the only HTTP surface
/// that reaches `IVisitorContactDetailRepository`. Operator-only, the same `"RequireOperatorIdentity"`
/// policy every conversation-scoped route in this file's neighbours (`NoteEndpoints`,
/// `ChannelIdentityEndpoints`) already use - there is no visitor variant of any of these three
/// handlers to route to, by construction.
/// </summary>
public static class ContactDetailEndpoints
{
    public static void MapContactDetailEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversations/{conversationId:guid}/contact-details")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleListAsync);
        group.MapPost("", HandleRecordAsync);
        group.MapDelete("/{contactDetailId:guid}", HandleDeleteAsync);
    }

    private static async Task<IResult> HandleListAsync(
        Guid conversationId,
        ListVisitorContactDetailsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListVisitorContactDetails(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ContactDetailsResponse([.. result.Value.Select(ToDto)]));
    }

    private static async Task<IResult> HandleRecordAsync(
        Guid conversationId,
        RecordContactDetailRequest request,
        RecordVisitorContactDetailHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RecordVisitorContactDetail(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId),
                request.Kind ?? string.Empty, request.Value ?? string.Empty),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ContactDetailDto(
                result.Value.Id, result.Value.Kind, result.Value.Value, result.Value.RecordedByOperatorId, result.Value.RecordedAt));
    }

    private static async Task<IResult> HandleDeleteAsync(
        Guid conversationId,
        Guid contactDetailId,
        DeleteVisitorContactDetailHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new DeleteVisitorContactDetail(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId),
                new VisitorContactDetailId(contactDetailId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static ContactDetailDto ToDto(VisitorContactDetailDto d) =>
        new(d.Id, d.Kind, d.Value, d.RecordedByOperatorId, d.RecordedAt);

    /// <summary>Nullable only because a client can omit either field - the handler decides an empty
    /// value or an unrecognised kind is an error, the same "validate downstream, translate the throw"
    /// split `NoteEndpoints.AddNoteRequest`'s own remarks describe for itself.</summary>
    public sealed record RecordContactDetailRequest(string? Kind, string? Value);

    public sealed record ContactDetailDto(Guid Id, string Kind, string Value, Guid RecordedByOperatorId, DateTimeOffset RecordedAt);

    public sealed record ContactDetailsResponse(IReadOnlyList<ContactDetailDto> ContactDetails);
}
