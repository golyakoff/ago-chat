namespace Ago.Chat.Application.UseCases.ListVisitorContactDetails;

/// <summary>What the console's own contact-details block needs to render one row and offer a delete
/// action - never the full <see cref="Domain.VisitorContactDetail"/> aggregate, matching every other
/// read-facing DTO in this codebase.
///
/// <para>`23-09`: <paramref name="RecordedByOperatorId"/> is nullable, and <paramref name="Source"/>/
/// <paramref name="Verified"/> are new - see <see cref="Domain.VisitorContactDetail"/>'s own remarks.
/// The console panel renders <paramref name="Source"/> rather than the raw operator id either way, so
/// a null <paramref name="RecordedByOperatorId"/> never reaches the screen as an empty cell or a
/// fabricated name - it simply is not the field the row's byline is built from.</para></summary>
public sealed record VisitorContactDetailDto(
    Guid Id, string Kind, string Value, Guid? RecordedByOperatorId, string Source, bool Verified, DateTimeOffset RecordedAt);
