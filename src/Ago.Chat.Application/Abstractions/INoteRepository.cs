using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for <see cref="ConversationNote"/> - shaped by the two use cases that need it
/// (`AddConversationNoteHandler`, `GetConversationNotesHandler`), never a generic
/// <c>IRepository&lt;T&gt;</c> (clean-architecture.md).
///
/// <para><b>This is the leak-proofing, made structural.</b> This interface shares no method, no base
/// type and no implementation with <see cref="IConversationRepository"/> or
/// <see cref="IConversationReadStore"/> - the ports <c>GetConversationHistoryHandler</c>'s visitor and
/// operator entry points both depend on. There is no line of code inside either of those handlers, or
/// anything they call, that can reach this interface at all: not a filtered-out branch, an absent one.
/// `18-04`'s own backlog item argues this is what "structurally incapable" has to mean in code, not
/// just in a diagram.</para>
/// </summary>
public interface INoteRepository
{
    Task SaveAsync(ConversationNote note, CancellationToken cancellationToken);

    /// <summary>Every note on one conversation, oldest first - an operator's own working notes,
    /// small and bounded (nobody writes thousands of notes on one conversation), so this is a plain
    /// unbounded list the same way <see cref="IConversationRepository.GetWaitingForSiteAsync"/>'s own
    /// remarks justify for a small, bounded read.</summary>
    Task<IReadOnlyList<ConversationNote>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken);

    // `16-02`: no DeleteForConversationAsync here. Ago.Chat.Worker's erasure jobs never go through
    // this Application-layer port at all - ConversationErasureQuery's own remarks establish "raw
    // Npgsql, forward-only" as the deliberate shape for every table an erasure sweep touches, so the
    // conversation_notes delete lives there (ConversationErasureQuery.DeleteNotesForConversationAsync),
    // called directly by ConversationErasureJob, the same way DeleteAttachmentsAsync already handles
    // attachments without going through IAttachmentRepository.
}
