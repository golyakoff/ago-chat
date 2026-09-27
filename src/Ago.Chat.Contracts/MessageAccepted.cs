namespace Ago.Chat.Contracts;

/// <summary>
/// docs/architecture/messaging.md's <c>MessageAccepted</c> topic, keyed by <see cref="ConversationId"/>.
/// No message body - a consumer that needs it reads <c>GetConversationHistory</c> instead; the payload
/// stays small on purpose. Mapped from <c>Ago.Chat.Domain.MessageAdded</c> in
/// <c>Ago.Chat.Application/Mapping</c>, never constructed from Domain directly.
///
/// <para><b>`26-215`/`adr/0186`: <see cref="TenantZone"/> joins this contract</b> so the ago-analytics
/// rollup (`26-213`'s ingest) can attribute an accepted message to its tenant-local day, the identical
/// fact <see cref="ConversationOpened"/> already carries and resolved the same way at every one of this
/// event's several publish sites - each already-loaded (or newly-added) cached
/// <c>GetSiteConfigByIdHandler</c>/<c>SiteConfigDto</c> read's own <c>TimeZone</c>, falling back to
/// <c>"Europe/Moscow"</c> where no site row can be resolved. Additive within `Version` 1
/// (`messaging.md`'s versioning rule) - every existing consumer of this topic (fan-out, unread
/// counters, `14-04`'s offline auto-reply) reads only the fields it already knew by name.</para>
/// </summary>
public sealed record MessageAccepted(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    Guid SiteId,
    Guid CorrelationId,
    Guid ConversationId,
    string AuthorKind,
    int Sequence,
    string TenantZone);
