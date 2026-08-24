using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// Domain event -> integration event -> <see cref="EventEnvelope"/>, `11-01`'s own repeat of the
/// pattern <see cref="ConversationClosedMapper"/>/<see cref="AttachmentConfirmedMapper"/> already
/// established (clean-architecture.md: mapping happens in Application when writing to the outbox,
/// never a shared type between Domain and Contracts). This is the first real *producer* of
/// <see cref="SiteSettingsChanged"/> - the contract and its consumer (`SiteCacheInvalidationConsumer`)
/// have existed and been tested since `3-04`, with nothing ever calling this mapper until now.
///
/// Unlike <see cref="ConversationClosedMapper"/> (whose contract carries no id of its own, so the
/// envelope reuses the conversation's id), <see cref="SiteSettingsChanged"/> declares its own
/// <c>MessageId</c> field - <see cref="Site.UpdateWidgetConfig"/> can run more than once per site, so
/// there is no single natural identity to reuse the way a once-only event like `ConversationClosed`/
/// `AttachmentReady` has; a fresh id from <see cref="IIdGenerator"/> is what both the contract and the
/// envelope share instead.
/// </summary>
public static class SiteWidgetConfigUpdatedMapper
{
    public static EventEnvelope ToEnvelope(SiteWidgetConfigUpdated domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new SiteSettingsChanged(
            MessageId: messageId,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            PublicKey: domainEvent.PublicKey);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(SiteSettingsChanged),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
