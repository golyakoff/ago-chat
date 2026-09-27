namespace Ago.Chat.Contracts;

/// <summary>
/// `6-02`'s webhook trigger for `6-05`'s eventual dispatcher - an operator closed the conversation via
/// `POST /api/v1/conversations/{id}/close`. Named differently from the domain event
/// <c>Ago.Chat.Domain.ConversationClosed</c> on purpose - the same domain-event/contract naming split
/// `ConversationAssigned`/`ConversationAssignedToOperator` and `ConversationReleased`/
/// `ConversationReleasedToQueue` already established, so `ConversationClosedMapper` using both types
/// side by side never collides on a bare name (`Ago.Chat.Application.Mapping`, keeping the literal
/// `ConversationClosedMapper` name the backlog item asked for, since the mapper's own namespace has no
/// collision to avoid - only the domain-event/contract pair does). Carried only
/// <see cref="ConversationId"/> and <see cref="ClosedAt"/> until `26-215` - no visitor/operator
/// identity, since a webhook receiver only needs to know which conversation closed, not who closed it
/// (`6-02`'s own scope note).
///
/// <para><b>`26-215`/`adr/0186`: <see cref="SiteId"/> and <see cref="TenantZone"/> join this contract</b>
/// so the ago-analytics rollup (`26-213`'s ingest) can attribute a close to a site and its tenant-local
/// day - the same two facts <see cref="ConversationOpened"/> already carries for a conversation's own
/// start, resolved the identical way (each closing handler's own cached
/// <c>GetSiteConfigByIdHandler</c> read, falling back to <c>"Europe/Moscow"</c> for the same
/// "site vanished in the one impossible instant between load and read" edge case
/// <c>MessageBatchWriter</c>'s own remarks already accept for <c>RetentionClass</c>). Additive within
/// `Version` 1 (`messaging.md`'s own versioning rule: "a new field is fine" within a version) - the
/// existing `6-02`/`6-05` webhook consumer only ever reads <see cref="ConversationId"/>/
/// <see cref="ClosedAt"/> by name, so nothing breaks for it. No separate <c>TenantId</c>:
/// `data-model.md` names <c>sites</c> itself "the tenant" in this codebase - <see cref="SiteId"/>
/// already is the tenant identifier, and a second column holding the same value would not be a new
/// fact.</para>
/// </summary>
public sealed record ConversationEnded(Guid ConversationId, DateTimeOffset ClosedAt, Guid SiteId, string TenantZone);
