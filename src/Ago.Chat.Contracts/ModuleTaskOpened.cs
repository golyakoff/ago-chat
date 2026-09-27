namespace Ago.Chat.Contracts;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): the analytics pipeline's own fact for a module task
/// (booking flow) opening. Named <c>ModuleTaskOpened</c>, deliberately different from the domain event
/// <c>Ago.Chat.Domain.ModuleTaskStarted</c> it is mapped from - the same domain-event/contract naming
/// split <c>ConversationStarted</c>/<c>ConversationOpened</c> already established, so
/// <c>ModuleTaskOpenedMapper</c> can bring both types into scope with no alias
/// (`feedback_no_type_alias_for_namespace_collisions`).
///
/// <para><b>The funnel rollup's "flow started" input.</b> The ago-analytics ingest lifts the envelope
/// identifiers (event id, occurred-at, site, tenant zone) into promoted ClickHouse columns and keeps this
/// body verbatim; the aggregator's <c>RawEnvelopeDecoder</c> reads <see cref="ModuleTaskId"/> and
/// <see cref="ModuleKey"/> from the body and folds one "flow started" per task into the funnel rollup for
/// the task's open day.</para>
///
/// <para><see cref="OccurredAt"/> is the task's open instant, so the raw layer buckets it into the
/// tenant-local day the flow started - the funnel report's own window key
/// (<c>module_tasks.opened_at</c>). Carries <see cref="SiteId"/> and <see cref="TenantZone"/> (the
/// analytics envelope identifiers), <see cref="ModuleTaskId"/> and <see cref="ModuleKey"/> - only ids and
/// the opaque module key, no message body or personal content on the wire (`messaging.md` /
/// `personal-data.md`). <c>MessageId</c> is the task id (a task opens once per id), `Version` 1.</para>
/// </summary>
public sealed record ModuleTaskOpened(
    Guid ConversationId,
    Guid SiteId,
    Guid ModuleTaskId,
    string ModuleKey,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string TenantZone);
