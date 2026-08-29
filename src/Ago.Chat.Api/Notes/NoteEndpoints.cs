using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.AddConversationNote;
using Ago.Chat.Application.UseCases.GetConversationNotes;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Notes;

/// <summary>
/// `18-04`: `GET`/`POST /api/v1/conversations/{conversationId}/notes` - the only HTTP surface that
/// reaches <c>INoteRepository</c> (see that interface's own remarks on why that narrowness matters).
/// Operator-only, the same <c>"RequireOperatorIdentity"</c> policy every conversation-scoped route in
/// this file's neighbour (<c>ConversationsEndpoints</c>) already uses - there is no visitor variant of
/// either handler to route to, by construction.
/// </summary>
public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversations/{conversationId:guid}/notes")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPost("", HandlePostAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid conversationId, GetConversationNotesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetConversationNotes(new ConversationId(conversationId), user.GetSiteId(), user.GetOperatorId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new NotesResponse([.. result.Value.Select(ToDto)]));
    }

    private static async Task<IResult> HandlePostAsync(
        Guid conversationId,
        AddNoteRequest request,
        AddConversationNoteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new AddConversationNote(new ConversationId(conversationId), user.GetSiteId(), user.GetOperatorId(), request.Body ?? string.Empty),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new NoteDto(result.Value.Id, result.Value.AuthorId, result.Value.Body, result.Value.CreatedAt));
    }

    private static NoteDto ToDto(ConversationNoteDto n) => new(n.Id, n.AuthorId, n.Body, n.CreatedAt);

    /// <summary>Nullable only because a client can omit it - the handler decides an empty body is an
    /// error, the same "validate downstream, translate the throw" split `CannedResponseEndpoints`'s
    /// own remarks describe for itself.</summary>
    public sealed record AddNoteRequest(string? Body);

    public sealed record NoteDto(Guid Id, Guid AuthorId, string Body, DateTimeOffset CreatedAt);

    public sealed record NotesResponse(IReadOnlyList<NoteDto> Notes);
}
