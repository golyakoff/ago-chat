using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-10`: the write side of a conversation's block state - raw SQL, never through the
/// <see cref="Conversation"/> aggregate, mirroring <see cref="IErasureRequestRepository"/>'s own family
/// exactly (<c>ConversationConfiguration</c>'s remarks on <c>ErasureRequestedAt</c> give the reasoning
/// in full: this repository's <c>GetByIdAsync</c> loads the whole aggregate, messages included, so
/// routing a block through it would both load a conversation's full history to flip two columns and
/// race this row's `xmin` against every ordinary message send).
///
/// <para><b>One statement, not two</b>, the identical <c>24-13</c> shape <see cref="IErasureRequestRepository"/>'s
/// own remarks describe: each method below is a single SQL statement built from data-modifying CTEs -
/// the first conditionally updates <c>conversations.blocked_at</c>/<c>blocked_by</c> (the row lock that
/// condition takes is what makes two concurrent requests race-free under Postgres's read-committed
/// rules), and the second inserts the matching <c>conversation_block_records</c> row only when the first
/// actually changed a row. There is no window in which the flag changed but the act was not recorded,
/// and no explicit <c>BEGIN</c>/<c>COMMIT</c> is needed to get that guarantee.</para>
/// </summary>
public interface IConversationBlockRepository
{
    /// <summary>Freezes one conversation against processing, scoped to <paramref name="siteId"/> the
    /// same way <see cref="IErasureRequestRepository.RequestConversationErasureAsync"/> is - a
    /// conversation belonging to a different site answers <see cref="ConversationBlockOutcome.NotFound"/>,
    /// never a distinct "wrong tenant" outcome (the same not-found-not-forbidden choice every other
    /// per-conversation check in this codebase makes for a resource outside the caller's tenant).</summary>
    Task<ConversationBlockOutcome> BlockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId blockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The reverse act - <see cref="ConversationBlockOutcome.AlreadyInState"/> here means the
    /// conversation was not blocked to begin with, not that it was already unblocked (there is no
    /// third state to distinguish - <see cref="Conversation.IsBlocked"/>'s own remarks).</summary>
    Task<ConversationBlockOutcome> UnblockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId unblockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>The three-way answer <see cref="IConversationBlockRepository"/>'s own methods need and
/// <see cref="IErasureRequestRepository"/>'s bare <see langword="bool"/> does not: erasure has only one
/// direction to request, so "found, and not already requested" collapses into one outcome. Blocking has
/// two real failure shapes worth telling apart on the wire - <see cref="ConversationErrors.NotFound"/>
/// and <see cref="ConversationErrors.ConversationAlreadyBlocked"/>/<see cref="ConversationErrors.ConversationNotBlocked"/>
/// have different remedies, so the repository must say which one happened rather than a caller
/// re-deriving it from a second query.</summary>
public enum ConversationBlockOutcome
{
    /// <summary>No conversation with this id exists for this site.</summary>
    NotFound,

    /// <summary>The conversation exists but was already in the state this call asked to move it out of
    /// (already blocked, for <see cref="IConversationBlockRepository.BlockAsync"/>; already unblocked -
    /// which is to say, not currently blocked - for <see cref="IConversationBlockRepository.UnblockAsync"/>).</summary>
    AlreadyInState,

    /// <summary>The requested transition happened, and its own <c>conversation_block_records</c> row was
    /// written alongside it.</summary>
    Applied,
}
