namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

public sealed record RecordedVisitorContactDetail(
    Guid Id, Guid VisitorId, string Kind, string Value, Guid RecordedByOperatorId, DateTimeOffset RecordedAt);
