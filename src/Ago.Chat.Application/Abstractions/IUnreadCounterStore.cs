using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-109`: the atomic, raw-SQL twin of <see cref="Conversation.IncrementUnreadCount"/> - see that
/// method's own remarks for the exact visitor-vs-operator branching and the
/// <c>sequence &gt; OperatorLastReadSequence</c> watermark condition this mirrors verbatim, one
/// conditional <c>UPDATE</c> per call rather than a load-mutate-save through the aggregate.
///
/// Exists beside <see cref="IConversationRepository"/>, not as a method on it - the identical split
/// <see cref="IOperatorCapacity"/> and <see cref="IConversationAttachmentBudget"/> already establish
/// for "a column with its own atomic compare-and-set writer, deliberately never loaded or mutated
/// through the aggregate it also lives on." The reason here is `25-109`'s own root-cause finding:
/// an EF load-mutate-save through <see cref="Conversation"/> stakes this row's own <c>xmin</c> against
/// every other writer of the *same* row for the life of the caller's transaction, regardless of which
/// column either side actually touches - concretely, <c>Ago.Chat.Infrastructure.Postgres.Pipeline.
/// MessageBatchWriter</c>'s own per-flush <c>SaveChangesAsync</c>, which starts a several-hundred-
/// millisecond, multi-conversation load window with this exact row exposed, unmodified, for its whole
/// duration. This port's write never contends for that token: it recomputes its own condition from
/// whatever <c>operator_last_read_sequence</c> reads *at the instant of the UPDATE*, under Postgres's
/// own row lock, rather than comparing against a value read earlier - strictly safer than the
/// EF-tracked version against a concurrent <see cref="Conversation.MarkReadByOperator"/>, not merely
/// different.
/// </summary>
public interface IUnreadCounterStore
{
    /// <summary>
    /// Applies one message's worth of <see cref="Conversation.IncrementUnreadCount"/> directly against
    /// the row, in one round trip. Participates in the caller's own ambient <see cref="IUnitOfWork"/>
    /// transaction automatically when one is open, exactly as <see cref="IOperatorCapacity"/>'s own
    /// remarks describe for <c>ExecuteSqlInterpolatedAsync</c> - <c>RecordUnreadMessageHandler</c>
    /// relies on precisely that to commit this write atomically with the inbox-dedup row it stages
    /// alongside it (`adr/0017`).
    /// </summary>
    Task IncrementAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, int sequence, CancellationToken cancellationToken);
}
