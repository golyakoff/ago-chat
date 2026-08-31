using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Mapping;

/// <summary>
/// `14-15`: <c>Ago.Chat.Domain.PhoneVerificationCodeIssued</c> -&gt; <see cref="PhoneVerificationDeliveryRequested"/>
/// -&gt; <see cref="EventEnvelope"/>, the same "mapping happens in Application when writing to the outbox"
/// shape every mapper in this folder already establishes (`OperatorRemovedMapper`'s own remarks). A fresh
/// <see cref="IIdGenerator"/> id for the envelope's own <c>MessageId</c> and <c>CorrelationId</c>, the
/// same uniform-across-every-mapper rule that file states, rather than reusing
/// <see cref="PendingPhoneVerificationId"/> case-by-case.
/// </summary>
public static class PhoneVerificationCodeIssuedMapper
{
    public static EventEnvelope ToEnvelope(PhoneVerificationCodeIssued domainEvent, IIdGenerator idGenerator)
    {
        var messageId = idGenerator.NewId(domainEvent.OccurredAt);
        var contract = new PhoneVerificationDeliveryRequested(
            PendingPhoneVerificationId: domainEvent.PendingPhoneVerificationId.Value,
            SiteId: domainEvent.SiteId.Value,
            Phone: domainEvent.Phone,
            Code: domainEvent.Code,
            DeliveryMethod: domainEvent.DeliveryMethod.ToString(),
            CorrelationId: idGenerator.NewId(domainEvent.OccurredAt),
            OccurredAt: domainEvent.OccurredAt);

        return new EventEnvelope(
            MessageId: messageId,
            Type: nameof(PhoneVerificationDeliveryRequested),
            Version: 1,
            // Keyed on the pending verification's own id, not the phone - `messaging.md`'s "partition key
            // is a first-class field because ordering-per-key is a guarantee we depend on"; nothing here
            // needs two sends for the same phone ordered against each other (each is its own independent
            // request), but every retry/redelivery of *this one* request must land on the same logical
            // partition, which the request's own id already guarantees uniquely.
            PartitionKey: contract.PendingPhoneVerificationId.ToString(),
            OccurredAt: contract.OccurredAt,
            CorrelationId: contract.CorrelationId,
            Payload: JsonSerializer.Serialize(contract));
    }
}
