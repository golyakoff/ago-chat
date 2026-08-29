using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationNotes;

/// <summary>
/// `18-04`. Operator-only by construction: there is no `HandleAsVisitorAsync` twin, and no route in
/// `Ago.Chat.Api` maps this to anything but `"RequireOperatorIdentity"`
/// (`NoteEndpoints.MapNoteEndpoints`) - the only entry point into <see cref="INoteRepository"/> that
/// exists anywhere in this codebase's call graph. See <see cref="INoteRepository"/>'s own remarks for
/// why that absence is the actual guarantee, not a filter inside this handler.
///
/// <para>Gated by <see cref="Permission.ConversationRead"/> - a note is read context for whoever can
/// already read the conversation, the same reasoning <see cref="Permission.ConversationNoteWrite"/>'s
/// own remarks give for reusing it rather than adding a third permission just for reading.</para>
/// </summary>
public sealed class GetConversationNotesHandler(
    IConversationReadStore readStore, INoteRepository notes, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<ConversationNoteDto>>> HandleAsync(
        GetConversationNotes query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await readStore.GetByIdAsync(query.ConversationId, query.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        var items = await notes.GetForConversationAsync(query.ConversationId, cancellationToken);

        return Result<IReadOnlyList<ConversationNoteDto>>.Success(
            items.Select(n => new ConversationNoteDto(n.Id.Value, n.AuthorId.Value, n.Body, n.CreatedAt)).ToList());
    }
}
