namespace Ago.Chat.Contracts;

/// <summary>
/// `23-11`'s own Done-when: "the tenant can read the reveal record, and the screen says what it is
/// for and what it is not." `GET /api/v1/sites/{siteId}/contact-reveals`'s response body - one keyset
/// page of who revealed a contact detail, when, and through which surface. <paramref name="NextBeforeId"/>
/// is the value to pass back as `?before=` for the following page, and <see langword="null"/> once the
/// oldest row has been reached. The same shape <see cref="AccessRecordsResponse"/> already establishes
/// for its own sibling audit read.
/// </summary>
public sealed record ContactRevealsResponse(IReadOnlyList<ContactRevealDto> Reveals, Guid? NextBeforeId);

/// <summary>
/// One reveal, as read back. Carries only who/what/when/which-surface - never the contact detail's
/// own value (`IContactRevealRepository`'s own remarks: "record that a reveal happened, not what was
/// revealed").
/// </summary>
public sealed record ContactRevealDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid ContactDetailId,
    Guid OperatorId,
    string Surface);
