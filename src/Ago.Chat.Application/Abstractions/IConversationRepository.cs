using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// The write-side port for the <see cref="Conversation"/> aggregate. Shaped by the use cases that
/// need it, never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md) - <see
/// cref="GetActiveForVisitorAsync"/> exists because <c>StartConversation</c> needs exactly that
/// question answered, not because a generic query method happened to be available.
/// </summary>
public interface IConversationRepository
{
    Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);

    /// <summary>The visitor's own not-yet-closed conversation, if one exists - what
    /// <c>StartConversation</c> uses to resume instead of always starting a new one.</summary>
    Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken);

    /// <summary>Every conversation currently `Assigned` to this operator - `4-04`'s
    /// disconnect-grace-period release needs all of them, not just one.</summary>
    Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken);

    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken);
}
