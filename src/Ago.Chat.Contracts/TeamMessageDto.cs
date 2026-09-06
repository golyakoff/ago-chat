namespace Ago.Chat.Contracts;

/// <summary>
/// `23-32`: the team chat's own wire shape (api-design.md: "payload shapes live in
/// Ago.Chat.Contracts"), the team-chat sibling of <see cref="MessageDto"/>. A separate type rather
/// than reusing <see cref="MessageDto"/>: a team message has no conversation, no attachment, no
/// structured content and no author kind (the author is always an operator) - fields
/// <see cref="MessageDto"/> carries that would either be always-null noise here or (worse) tempt a
/// client into treating the two message kinds as interchangeable, when a team message is
/// deliberately invisible to a visitor and never reaches <see cref="MessageDto"/>'s own console
/// rendering path.
///
/// <see cref="AuthorDisplayName"/>/<see cref="AuthorEmail"/> are resolved at read time from the
/// author's current <c>operators</c> row (a live join, not a value stamped on the message) - a
/// display name is what a reader wants to see updated if it changes, unlike
/// <see cref="AuthorIsAdmin"/> below, which is a fact about a specific moment rather than an
/// identity. Both are <see langword="null"/> for the one row shape that carries neither
/// (<c>adr/0104</c>'s minted-demo-operator row, the same case <c>OperatorTeamMemberItem</c> already
/// documents).
///
/// <see cref="AuthorIsAdmin"/> is the label `23-32`'s own Goal requires ("the tenant is visible as
/// the tenant") - stamped at send time, deliberately not recomputed from the author's role today; see
/// <c>Ago.Chat.Domain.TeamMessage.AuthorIsAdmin</c>'s own remarks for why a historical message keeps
/// the label it earned when it was sent.
/// </summary>
public sealed record TeamMessageDto(
    Guid Id,
    int Sequence,
    Guid AuthorOperatorId,
    string? AuthorDisplayName,
    string? AuthorEmail,
    bool AuthorIsAdmin,
    string Body,
    DateTimeOffset CreatedAt,
    Guid? ClientMessageId = null);
