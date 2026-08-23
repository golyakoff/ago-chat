using Ago.Platform.Abstractions;

namespace Ago.Chat.Worker;

/// <summary>
/// <see cref="OccurredAt"/> is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, because
/// Npgsql returns <c>timestamptz</c> as a UTC-kinded <see cref="DateTime"/> over raw ADO.NET
/// (EF's own provider does the <see cref="DateTimeOffset"/> conversion itself; this dispatcher reads
/// the row directly for the <c>FOR UPDATE SKIP LOCKED</c> claim, so it takes the same shape
/// <c>ConversationReadStore</c>'s <c>MessageRow</c> already established for this exact reason).
/// <see cref="Ago.Chat.Worker"/> is a Host, not scanned by the "DateTime only in Infrastructure" arch
/// rule (naming-and-structure.md places outbox dispatch here on purpose), but the conversion still
/// happens before anything crosses into <see cref="ToEnvelope"/>'s <see cref="EventEnvelope"/>.
/// </summary>
internal sealed record ClaimedOutboxRow(
    Guid Id,
    DateTime OccurredAt,
    string Type,
    int Version,
    string Payload,
    string PartitionKey,
    Guid CorrelationId,
    int Attempts,
    string? TraceContext)
{
    public EventEnvelope ToEnvelope() => new(
        MessageId: Id,
        Type: Type,
        Version: Version,
        PartitionKey: PartitionKey,
        OccurredAt: new DateTimeOffset(DateTime.SpecifyKind(OccurredAt, DateTimeKind.Utc)),
        CorrelationId: CorrelationId,
        Payload: Payload);
}
