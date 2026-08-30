namespace Ago.Chat.Application.UseCases.ListVisitorContactDetails;

/// <summary>What the console's own contact-details block needs to render one row and offer a delete
/// action - never the full <see cref="Domain.VisitorContactDetail"/> aggregate, matching every other
/// read-facing DTO in this codebase.</summary>
public sealed record VisitorContactDetailDto(
    Guid Id, string Kind, string Value, Guid RecordedByOperatorId, DateTimeOffset RecordedAt);
