using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `16-02`: stamps `erasure_requested_at` on a `Site` or a `Conversation` - the one write both erase
/// endpoints perform, and the only thing they perform (`SiteErasureJob`/`ConversationErasureJob` do
/// the actual deletion later, off the timer, never inside a request).
///
/// <para><b>Its own port rather than a method on <see cref="ISiteRepository"/>/
/// <see cref="IConversationRepository"/>.</b> Both of those load the full aggregate -
/// <c>ConversationRepository.GetByIdAsync</c> includes every message the conversation has ever held -
/// and save it back under the row's own `xmin`. Routing a single-column flag through that path would
/// mean loading a conversation's entire history just to set one timestamp, and racing this row's
/// `xmin` against every ordinary message send landing on the same conversation in the meantime -
/// exactly the failure mode <see cref="Site"/>'s own `erasure_requested_at`/
/// <see cref="Conversation"/>'s own shadow property are deliberately kept off the aggregate to
/// avoid (see each `IEntityTypeConfiguration`'s own remarks). This port is the same "deliberately
/// reaches a row without going through its aggregate's usual load-mutate-save" shape
/// <see cref="IDemoTenantRepository"/> already established for exactly this reason.</para>
///
/// <para>Idempotent by design: a second call after the flag is already set is a no-op that preserves
/// the original request time, never an error and never a reset of the clock erasure's own completeness
/// story (the 30-day backup window, `15-02`/`adr/0050`) is measured from.</para>
/// </summary>
public interface IErasureRequestRepository
{
    /// <summary>
    /// Sets <c>sites.erasure_requested_at</c> if it is not already set. Returns <see langword="false"/>
    /// if no site with this id exists - the caller's <c>Site.NotFound</c> case - and
    /// <see langword="true"/> otherwise, whether this call was the one that set the flag or it was
    /// already set by an earlier request.
    /// </summary>
    Task<bool> RequestSiteErasureAsync(SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <c>conversations.erasure_requested_at</c> if it is not already set, scoped to
    /// <paramref name="siteId"/> as well as <paramref name="conversationId"/> - the same
    /// per-conversation site check <see cref="Ago.Chat.Application.UseCases.CloseConversation.CloseConversationHandler"/>'s route wiring gets for free
    /// from <c>user.GetSiteId()</c>, made explicit here so a conversation that exists but belongs to a
    /// different site is indistinguishable from one that does not exist at all, never a cross-tenant
    /// existence leak. Returns <see langword="false"/> when no matching row is found (either reason);
    /// the caller reports both as <c>Conversation.NotFound</c>.
    /// </summary>
    Task<bool> RequestConversationErasureAsync(
        ConversationId conversationId, SiteId siteId, DateTimeOffset requestedAt, CancellationToken cancellationToken);
}
