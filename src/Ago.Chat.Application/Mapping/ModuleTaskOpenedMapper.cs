using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `26-237`/`adr/0186` (module-flow funnel rollup): domain event -> integration event ->
/// <see cref="EventEnvelope"/>, the same pattern <see cref="ConversationOpenedMapper"/> establishes. The
/// domain event carries everything the analytics wire event needs (ids, module key, the open instant);
/// <paramref name="tenantZone"/> is not on the domain event (the funnel is a cross-cutting analytics
/// concern, not part of the module-task aggregate), so the caller
/// (<c>RouteConversationToModuleHandler.ApplyAndSaveAsync</c>, which already read the site for the
/// message-accepted envelope on the same save) passes it in.
/// </summary>
public static class ModuleTaskOpenedMapper
{
    public static EventEnvelope ToEnvelope(ModuleTaskStarted domainEvent, string tenantZone, IIdGenerator idGenerator)
    {
        var contract = new ModuleTaskOpened(
            ConversationId: domainEvent.ConversationId.Value,
            SiteId: domainEvent.SiteId.Value,
            ModuleTaskId: domainEvent.ModuleTaskId.Value,
            ModuleKey: domainEvent.ModuleKey.Value,
            OccurredAt: domainEvent.OccurredAt,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            TenantZone: tenantZone);

        return new EventEnvelope(
            // A task opens exactly once per id, so the task id is a safe redelivery-idempotency key -
            // the same "start runs once per aggregate id" reasoning ConversationOpenedMapper uses.
            MessageId: contract.ModuleTaskId,
            Type: nameof(ModuleTaskOpened),
            Version: 1,
            PartitionKey: contract.ConversationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
