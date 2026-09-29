namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0189`/`26-275` slice #3: this product's own copy of the wire shape the calendar publishes once
/// it has erased its own half of a person (Option A, issue 1815) - today
/// <c>Ago.Calendar.Contracts.PersonErased</c>, independently declared here, never shared, the identical
/// reasoning <see cref="PersonRegisteredWireContract"/>'s own remarks give for the opposite-direction
/// event.
///
/// <para>Property names match the source record exactly - PascalCase, no camelCase policy, mirroring
/// <c>Ago.Calendar.Contracts.PersonErased</c> byte-for-byte:
/// <c>PersonErased(Guid PersonId, Guid AccountId, DateTimeOffset OccurredAt, Guid CorrelationId)</c>
/// (`adr/0189`).</para>
/// </summary>
internal sealed record PersonErasedWireContract(Guid PersonId, Guid AccountId, DateTimeOffset OccurredAt, Guid CorrelationId);
