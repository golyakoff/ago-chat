namespace Ago.Chat.Application.UseCases.GetPersonConversations;

/// <summary>
/// `26-269`: one of this person's conversations, on the wire. Field names are the contract - the
/// consoles' own TypeScript/Kotlin copies match them verbatim under the default camelCase policy
/// (`PersonProfileDto`'s own remarks state the same rule for the sibling person read).
///
/// <see cref="IsActive"/> restates <see cref="State"/> as the one boolean a client actually branches
/// on ("show the composer, or open read-only") rather than making every consumer re-derive
/// <c>state !== "Closed"</c> for itself - the design doc's own §5/§8#2 flags this as the shape choice
/// worth pinning down, and duplicating a one-line predicate in TypeScript, Kotlin and here is a smaller
/// risk than three copies quietly drifting apart on what "active" means. <see cref="State"/> stays on
/// the wire too, unreduced, for the same reason the phone-status glyph collapses only a list row's
/// *presentation* of "actionable or not" while leaving the two underlying facts as two facts (design
/// doc §3.3) - a caller that needs the real state (e.g. to distinguish <c>Waiting</c> from
/// <c>Assigned</c>) is never left with only the collapsed view.
///
/// <see cref="ClosedAt"/> is <see langword="null"/> for a conversation still open - <see cref="State"/>
/// (or <see cref="IsActive"/>) already says which, the identical choice <c>VisitorHistoryConversationDto</c>
/// makes for itself.
/// </summary>
public sealed record PersonConversationDto(
    Guid ConversationId,
    string State,
    bool IsActive,
    DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset LastActivityAt);
