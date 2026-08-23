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

    /// <summary>
    /// `5-07`: every conversation currently `Waiting` for this site - the console's queue view's
    /// other half. Returns the full aggregate via EF, same as <see cref="GetAssignedToOperatorAsync"/>
    /// right above it, not a `Dapper`-backed `IConversationReadStore` query: adr/0004's "EF for
    /// writes, Dapper for reads" rule is the default, not an absolute - `GetAssignedToOperatorAsync`
    /// already established the precedent of a plain listing read going through this write-side
    /// repository when the list is small and bounded (one site's waiting queue, one operator's own
    /// capacity) rather than the kind of paginated, potentially-large read `IConversationReadStore`
    /// exists for (message history). Introducing a second read pattern for symmetry alone would cost
    /// more than it buys here.
    /// </summary>
    Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    Task SaveAsync(Conversation conversation, CancellationToken cancellationToken);
}
