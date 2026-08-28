namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ConversationErasureJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class ConversationErasureJobOptions
{
    public const string SectionName = "ConversationErasureJob";

    /// <summary>How often a sweep cycle runs. Erasure is already asynchronous by contract (the HTTP
    /// endpoint returns `202 Accepted` before anything is deleted), so this is a balance between "the
    /// console's completion poll does not wait unreasonably long" and "do not add needless load to the
    /// conversations/messages tables" - an operational default, not a measurement (`CLAUDE.md`: "do
    /// not invent numbers").</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many conversations one sweep cycle claims. Small relative to
    /// <see cref="OutboxPruneJobOptions.BatchSize"/> - unlike that job's pure-SQL delete, erasing one
    /// conversation reaches MinIO per attachment, so the per-item cost here is an external I/O call,
    /// not a row.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Rows removed by one `messages` `DELETE ... LIMIT` statement -
    /// <see cref="ConversationErasureQuery.DeleteMessageBatchAsync"/>'s own bound, the same
    /// "a single unbounded DELETE on a hot table is its own incident" reasoning
    /// <see cref="OutboxPruneJobOptions.BatchSize"/> states.</summary>
    public int MessageBatchSize { get; set; } = 500;

    /// <summary>A safety valve on one conversation's own message-draining loop, the same role
    /// <see cref="OutboxPruneJobOptions.MaxBatchesPerCycle"/> plays for the whole cycle: bounds how
    /// many batches <see cref="ConversationErasureJob.EraseConversationAsync"/> will issue for a
    /// single conversation before yielding, so one exceptionally large conversation cannot hold up
    /// every other claimed conversation behind it in the same cycle. A conversation not fully drained
    /// within this bound simply stays flagged - <c>erasure_requested_at</c> is untouched - and the
    /// next cycle's claim finds it again and continues; nothing needs to remember how far it got,
    /// because "how far" is exactly what is left in the table (`16-02`'s own design note on resumability).
    /// At the defaults this still drains up to 50,000 messages per conversation per cycle.</summary>
    public int MaxMessageBatchesPerConversation { get; set; } = 100;
}
