namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>`23-09`: <paramref name="RecordedByOperatorId"/> is nullable and <paramref name="Source"/>/
/// <paramref name="Verified"/> are new - a visitor-supplied row has no operator behind it and is never
/// verified, the same widening <see cref="Domain.VisitorContactDetail"/>'s own remarks describe for
/// itself.</summary>
/// <summary>`25-58`: <paramref name="Assessment"/> is new - always `"Unset"` for a freshly recorded row,
/// since neither entry point this record serves ever produces a Phone/Email row an operator has already
/// confirmed or flagged (there is no such action at record time) - see
/// <see cref="Domain.VisitorContactDetailAssessment"/>'s own remarks.</summary>
public sealed record RecordedVisitorContactDetail(
    Guid Id, Guid VisitorId, string Kind, string Value, Guid? RecordedByOperatorId, string Source, bool Verified,
    DateTimeOffset RecordedAt, string Assessment);
