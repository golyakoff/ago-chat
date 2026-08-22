using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for the <see cref="Attachment"/> aggregate - its own port, not folded into
/// <see cref="IConversationRepository"/>, because `5-03` made <see cref="Attachment"/> its own
/// aggregate root with its own transaction boundary (see that type's own remarks).
/// </summary>
public interface IAttachmentRepository
{
    Task<Attachment?> GetByIdAsync(AttachmentId id, CancellationToken cancellationToken);

    Task SaveAsync(Attachment attachment, CancellationToken cancellationToken);
}
