using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `25-160`: maps <see cref="Site.SubmitLogoUpload"/>'s own domain event to
/// <see cref="SiteLogoValidationRequested"/> - not <c>SiteSettingsChanged</c> (that domain event's own
/// remarks explain why). The same shape <see cref="SiteBrandCompanyNameUpdatedMapper"/> establishes -
/// a fresh <see cref="IIdGenerator"/> id for both the contract's own <c>MessageId</c> and the envelope,
/// since a site can submit more than one logo upload (`25-160`'s own 5-per-day rate limit).
/// </summary>
public static class SiteLogoUploadSubmittedMapper
{
    public static EventEnvelope ToEnvelope(SiteLogoUploadSubmitted domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new SiteLogoValidationRequested(
            MessageId: messageId,
            OccurredAt: domainEvent.OccurredAt,
            SiteId: domainEvent.SiteId.Value,
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            ObjectKey: domainEvent.ObjectKey,
            ContentType: domainEvent.ContentType);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(SiteLogoValidationRequested),
            Version: 1,
            PartitionKey: contract.SiteId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
