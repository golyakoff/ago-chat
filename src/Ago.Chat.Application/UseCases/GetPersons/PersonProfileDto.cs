namespace Ago.Chat.Application.UseCases.GetPersons;

/// <summary>
/// `adr/0184`: what the account's person registry says about one person, for display. The wire shape
/// <c>ago-console</c> and <c>ago-android</c> read by person id and merge onto the calendar's own booking
/// rows (decision 4: "reads are display-only and console-side"). Field names are the contract - the
/// consoles' own TypeScript/Kotlin copies match them verbatim under the default camelCase policy.
/// </summary>
/// <param name="PersonId">Chat's own visitor id - the one id every product references.</param>
/// <param name="DisplayName">The most recently recorded <c>Name</c> contact detail, or
/// <see langword="null"/> when nobody has recorded one. Never invented from a phone number.</param>
/// <param name="Channels">Every <c>Phone</c>/<c>Email</c> detail on file, masked per the account's own
/// contact-visibility rung exactly the way the conversation panel masks them - the identical
/// <c>ListVisitorContactDetailsHandler</c> rule, so a name never reveals what a masked panel hides.</param>
public sealed record PersonProfileDto(
    Guid PersonId,
    string? DisplayName,
    IReadOnlyList<PersonContactChannelDto> Channels,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <param name="Kind"><c>Phone</c> or <c>Email</c> - <c>VisitorContactDetailKind</c>'s own wire name;
/// <c>Name</c> is folded into <see cref="PersonProfileDto.DisplayName"/> and never listed here.</param>
/// <param name="Masked">Whether <see cref="Value"/> is the masked display form. The console must not infer
/// this from the string's own shape.</param>
public sealed record PersonContactChannelDto(
    Guid Id,
    string Kind,
    string Value,
    bool Masked,
    bool Verified,
    string Assessment,
    DateTimeOffset RecordedAt);
