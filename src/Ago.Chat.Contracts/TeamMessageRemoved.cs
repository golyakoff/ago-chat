namespace Ago.Chat.Contracts;

/// <summary>
/// `23-33`: the team-chat sibling of <see cref="TeamMessagePosted"/> - docs/architecture/messaging.md's
/// own topic for the tombstone push, keyed by <see cref="SiteId"/> for the identical reason: two
/// removals in the same room must not be processed out of order by a competing consumer, and nothing
/// about this event needs ordering against any other room's.
///
/// <para>No message body, matching <see cref="TeamMessagePosted"/>'s own precedent - a consumer that
/// needs the (now-redacted) current state
/// (<c>Ago.Chat.Application.UseCases.ResolveTeamMessageRemovalDelivery.ResolveTeamMessageRemovalDeliveryTargetsHandler</c>)
/// reads the room's own history instead, by <see cref="Sequence"/>, rather than this payload carrying
/// a second copy of a fact the write side already committed. Mapped from raw values in
/// <c>Ago.Chat.Application.Mapping.TeamMessageRemovedMapper</c>, called from
/// <c>Ago.Chat.Infrastructure.Postgres.TeamChatRepository.RemoveAsync</c> - there is no aggregate root
/// to raise a domain event from, the identical reason <see cref="TeamMessagePosted"/>'s own remarks
/// give.</para>
///
/// <para><b>Why a distinct event rather than publishing <see cref="TeamMessagePosted"/> again.</b> The
/// realtime push it drives is a genuinely different client-side action - replacing an already-rendered
/// message with a tombstone, not appending a new one - and the console's own transport-level dedup
/// (`SeenMessageIds`, keyed by message id) would silently drop a second <c>TeamMessageReceived</c> push
/// carrying the same <see cref="TeamMessageId"/>: that dedup exists precisely to collapse an operator's
/// own local echo against the fan-out copy of one post, and a removal is not a second delivery of the
/// same post. A distinct event name, a distinct client push method (<c>TeamMessageRemoved</c>, never
/// <c>TeamMessageReceived</c>), and a distinct client-side handler that updates the existing row by id
/// instead of deduplicating against it are what keep the two from colliding.</para>
/// </summary>
public sealed record TeamMessageRemoved(
    Guid TeamMessageId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    int Sequence);
