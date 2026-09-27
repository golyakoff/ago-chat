namespace Ago.Chat.Domain;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): raised by <see cref="Conversation.CloseModuleTask"/> -
/// analytics needs to know when a previously-opened module task closed, to feed the funnel rollup (the
/// "flows closed" count). Named <c>ModuleTaskClosed</c>, deliberately different from the integration event
/// <c>Ago.Chat.Contracts.ModuleTaskEnded</c> it maps to - the same domain-event/contract naming split
/// <c>ConversationClosed</c>/<c>ConversationEnded</c> already established, so <c>ModuleTaskEndedMapper</c>
/// can bring both types into scope with no alias.
///
/// <para><see cref="OccurredAt"/> is the close instant (the domain fact's own time), but
/// <see cref="OpenedAt"/> - the task's original open instant - is carried alongside <b>because the
/// analytics wire event buckets by the open day, not the close</b>: the funnel rollup pairs a close with
/// its start inside the open day's slice, so <c>ModuleTaskEndedMapper</c> stamps the envelope's
/// <c>OccurredAt</c> with <see cref="OpenedAt"/> (the same "bucket by where the period began" choice
/// <c>ConversationAssignmentClosed</c> makes for its interval's start). Carrying the open instant on the
/// event is what lets the mapper do that without re-reading the task.</para>
/// </summary>
public sealed record ModuleTaskClosed(
    ConversationId ConversationId,
    SiteId SiteId,
    ModuleTaskId ModuleTaskId,
    ModuleKey ModuleKey,
    DateTimeOffset OpenedAt,
    DateTimeOffset OccurredAt) : IDomainEvent;
