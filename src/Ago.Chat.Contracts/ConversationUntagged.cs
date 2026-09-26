namespace Ago.Chat.Contracts;

/// <summary>
/// `adr/0186` S1: the mirror of <see cref="ConversationTagged"/> - a tag was removed from this
/// conversation. Published from
/// <c>Ago.Chat.Infrastructure.Postgres.TagRepository.RemoveFromConversationAsync</c> on the identical
/// terms: one explicit transaction spanning the raw <c>delete</c> and the outbox insert, and never
/// published when the delete affected no row (removing a tag that was never applied, or already
/// removed, is a no-op - <see cref="ConversationTagged"/>'s own remarks state the identical reasoning).
/// </summary>
public sealed record ConversationUntagged(
    Guid ConversationId, Guid SiteId, Guid TagId, DateTimeOffset OccurredAt, Guid CorrelationId);
