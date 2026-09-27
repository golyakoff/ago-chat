namespace Ago.Chat.Contracts;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): the analytics pipeline's own fact for a module task
/// closing. Named <c>ModuleTaskEnded</c>, deliberately different from the domain event
/// <c>Ago.Chat.Domain.ModuleTaskClosed</c> it is mapped from - the same domain-event/contract naming
/// split <c>ConversationClosed</c>/<c>ConversationEnded</c> already established, so
/// <c>ModuleTaskEndedMapper</c> can bring both types into scope with no alias.
///
/// <para><b>The funnel rollup's "flow closed" input.</b> The aggregator's <c>RawEnvelopeDecoder</c> reads
/// <see cref="ModuleTaskId"/> and <see cref="ModuleKey"/> from the body and, within the task's open-day
/// slice, marks the matching started task as closed. That pairing only works if the close lands in the
/// same day-slice as its start, which is why -</para>
///
/// <para><b><see cref="OccurredAt"/> is the task's <em>open</em> instant, not its close.</b> The funnel
/// report windows by <c>module_tasks.opened_at</c> and counts a task as closed regardless of when it
/// closed, so this event's envelope <c>OccurredAt</c> is <see cref="OpenedAt"/> - it rolls up into the
/// tenant-local day the flow <em>started</em>, exactly the same "bucket by where the period began" choice
/// <c>ConversationAssignmentClosed</c> makes. It is emitted at close (that is when the transition is
/// known), staged to the outbox in the same unit of work as the close (rule 4). <see cref="ClosedAt"/> is
/// carried too for any future report that wants the real close instant - the raw layer retains it without
/// pre-quantization (`adr/0186`).</para>
///
/// <para>Carries only ids, the opaque module key and the two instants - no message body or personal
/// content (`messaging.md` / `personal-data.md`). <c>MessageId</c> is a fresh id per publish (a close is a
/// once-per-task transition, but minting a fresh id keeps the mapper uniform with the other analytics
/// mappers and the raw layer dedups on it anyway), `Version` 1.</para>
/// </summary>
public sealed record ModuleTaskEnded(
    Guid ConversationId,
    Guid SiteId,
    Guid ModuleTaskId,
    string ModuleKey,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string TenantZone);
