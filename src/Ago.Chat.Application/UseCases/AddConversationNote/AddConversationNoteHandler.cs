using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AddConversationNote;

/// <summary>
/// `18-04`. Existence/tenant-scope is checked against <see cref="IConversationReadStore.GetByIdAsync"/>
/// - the same lightweight summary lookup <c>GetConversationByIdHandler</c> uses - never
/// <see cref="IConversationRepository.GetByIdAsync"/>, which loads the whole aggregate including every
/// message. A note write has no reason to pull a conversation's full message history into memory just
/// to confirm the id is real and belongs to this site.
///
/// <para>Gated by <see cref="Permission.ConversationNoteWrite"/>, not <see cref="Permission.ConversationRead"/> -
/// see that permission's own remarks in <see cref="Permission"/>. Deliberately <b>not</b> restricted to
/// the conversation's currently-assigned operator the way <c>GetConversationHistoryHandler</c>'s
/// operator path is: a note is shared operational context for whoever ends up handling this
/// conversation, including after a `18-02` transfer, so any operator holding the site-scoped permission
/// may add one - the same site-wide-oversight shape <c>GetAllConversationsForSiteHandler</c> already
/// establishes for a different permission.</para>
/// </summary>
public sealed class AddConversationNoteHandler(
    IConversationReadStore readStore,
    INoteRepository notes,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<AddedConversationNote>> HandleAsync(
        AddConversationNote command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationNoteWrite, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to write notes on this site's conversations.");
        }

        var conversation = await readStore.GetByIdAsync(command.ConversationId, command.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var now = clock.UtcNow;
        ConversationNote note;
        try
        {
            note = ConversationNote.Write(
                new ConversationNoteId(idGenerator.NewId(now)), command.ConversationId, command.RequestedBy, command.Body, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.NoteInvalid(ex.Message);
        }

        await notes.SaveAsync(note, cancellationToken);

        return new AddedConversationNote(note.Id.Value, note.ConversationId.Value, note.AuthorId.Value, note.Body, note.CreatedAt);
    }
}
