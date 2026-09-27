namespace Ago.Chat.Domain;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): raised by <see cref="Conversation.StartModuleTask"/>
/// now that a real consumer exists - analytics needs to know when a module task (booking flow) was
/// opened, to feed the funnel rollup (the "flows started" count). Named <c>ModuleTaskStarted</c>,
/// deliberately different from the integration event <c>Ago.Chat.Contracts.ModuleTaskOpened</c> it maps
/// to - the same domain-event/contract naming split <c>ConversationStarted</c>/<c>ConversationOpened</c>
/// already established, so <c>ModuleTaskOpenedMapper</c> can bring both types into scope with no alias
/// (`feedback_no_type_alias_for_namespace_collisions`: rename the colliding type, never
/// <c>using Alias = ...</c>).
///
/// <para><see cref="OccurredAt"/> is the task's open instant - both the domain fact's time and the day the
/// funnel report windows by (<c>module_tasks.opened_at</c>).</para>
/// </summary>
public sealed record ModuleTaskStarted(
    ConversationId ConversationId,
    SiteId SiteId,
    ModuleTaskId ModuleTaskId,
    ModuleKey ModuleKey,
    DateTimeOffset OccurredAt) : IDomainEvent;
