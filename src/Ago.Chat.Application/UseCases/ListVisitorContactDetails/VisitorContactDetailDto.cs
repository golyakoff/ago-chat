namespace Ago.Chat.Application.UseCases.ListVisitorContactDetails;

/// <summary>What the console's own contact-details block needs to render one row and offer a delete
/// action - never the full <see cref="Domain.VisitorContactDetail"/> aggregate, matching every other
/// read-facing DTO in this codebase.
///
/// <para>`23-09`: <paramref name="RecordedByOperatorId"/> is nullable, and <paramref name="Source"/>/
/// <paramref name="Verified"/> are new - see <see cref="Domain.VisitorContactDetail"/>'s own remarks.
/// The console panel renders <paramref name="Source"/> rather than the raw operator id either way, so
/// a null <paramref name="RecordedByOperatorId"/> never reaches the screen as an empty cell or a
/// fabricated name - it simply is not the field the row's byline is built from.</para>
///
/// <para>`23-11`: <paramref name="Value"/> is <b>already masked</b> when the site's own
/// <see cref="Domain.ContactVisibility"/> rung is <see cref="Domain.ContactVisibility.MaskedWithReveal"/>
/// - <see cref="ListVisitorContactDetailsHandler"/>'s own remarks: the masking happens in the read
/// model, never in the console, so the real value is never present anywhere in this response, not
/// merely hidden by a client that could be told not to hide it. <paramref name="Masked"/> is what lets
/// the console tell the two cases apart without inferring it from the string's own shape (a real,
/// short value could otherwise look exactly like a masked one) - <see langword="true"/> means "call
/// the reveal endpoint to see the real value," <see langword="false"/> means <paramref name="Value"/>
/// already is the real value.</para></summary>
public sealed record VisitorContactDetailDto(
    Guid Id, string Kind, string Value, Guid? RecordedByOperatorId, string Source, bool Verified,
    DateTimeOffset RecordedAt, bool Masked);
