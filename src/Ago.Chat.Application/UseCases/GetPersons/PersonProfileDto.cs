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
/// <param name="EmojiCreature">One member of <see cref="Domain.VisitorEmojiDictionary.Creatures"/>,
/// paired with <paramref name="EmojiFood"/> - the identical operator-side memory aid
/// <see cref="Contracts.ConversationSummaryDto"/> already exposes (`25-56`), read here from the same
/// <see cref="Domain.Visitor.EmojiCreature"/> column rather than recomputed, since the pair is assigned
/// once at first contact and stored on the visitor, never derived fresh from the id. Additive/nullable
/// the same way every field `25-56` added is - <see langword="null"/> only for a visitor row that
/// predates the pair and has not yet been backfilled.</param>
/// <param name="EmojiFood">The other half of the pair - see <paramref name="EmojiCreature"/>'s own
/// remarks, which this parameter shares in full.</param>
public sealed record PersonProfileDto(
    Guid PersonId,
    string? DisplayName,
    IReadOnlyList<PersonContactChannelDto> Channels,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string? EmojiCreature = null,
    string? EmojiFood = null);

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
