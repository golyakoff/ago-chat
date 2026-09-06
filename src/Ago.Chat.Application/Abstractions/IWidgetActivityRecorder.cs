using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-07`: the write side of the funnel - "accumulate and flush", never a row write per call.
/// Application declares this port for the same reason it declares <see cref="IMessagePipeline"/>: the
/// two callers (<c>WidgetActivityEndpoints.HandleAsync</c> for loads/opens, <c>VisitorHub.JoinCoreAsync</c>
/// for a newly started conversation) need something they cannot do themselves - bump an in-memory
/// counter that a background service later flushes - and Application must never know that the
/// implementation (<c>Ago.Chat.Module.Pipeline.WidgetActivityAccumulator</c>) owns a
/// <c>ConcurrentDictionary</c>.
///
/// <para><b>Synchronous, unlike every other port in this file.</b> Every method here is a bare
/// in-memory increment - no lock held across an <c>await</c>, no I/O, nothing to cancel - so making
/// these <c>Task</c>-returning would only add an allocation for a continuation that always completes
/// synchronously. This is the literal meaning of the backlog item's own Scope: "It writes nothing
/// synchronously" describes what happens to Postgres, not what happens to this call - the caller
/// returns from these methods before any network or disk I/O has been considered at all.</para>
///
/// <para><b>Losing a call to this interface on a pod restart is an accepted, named cost</b>
/// (`docs/design/decisions.md` §3: "a batched flush that loses a few events on a pod restart is
/// acceptable"), not a bug to engineer around - the same tolerance <see cref="IMessagePipeline"/>'s own
/// batch writer does not have, because a lost *message* is a lost conversation turn and a lost *count*
/// is one visitor missing from a tenant's own approximate dashboard.</para>
/// </summary>
public interface IWidgetActivityRecorder
{
    /// <summary>One widget mount that produced a countable page load - `docs/backlog/23-07-*.md`'s own
    /// Scope: "one beacon per widget mount, sent from `ChatWidget.mount`". Never called for a refused
    /// origin - the caller checks that first (`WidgetActivityEndpoints.HandleAsync`'s own ordering,
    /// matching `AuthEndpoints.HandleVisitorSessionAsync`'s).</summary>
    void RecordLoad(SiteId siteId, DateTimeOffset now);

    /// <summary>One visitor opening the panel - at most once per session, per the widget's own
    /// bookkeeping (`ChatWidget`'s own `open()`), never once per click.</summary>
    void RecordOpen(SiteId siteId, DateTimeOffset now);

    /// <summary>One conversation actually started - called from <c>VisitorHub.JoinCoreAsync</c> when
    /// <c>StartConversationHandler</c> reports <c>IsNew</c>, never from a second beacon. Deliberately
    /// widget-scoped: a conversation `ReceiveChannelMessageHandler` starts for an external channel
    /// (SMS, Telegram, ...) has no widget load or open beside it in this same window, and folding it
    /// into this count would make the funnel's own ordering guarantee - conversations never exceeding
    /// opens - false for a channel-heavy tenant for a reason that has nothing to do with their widget.
    /// </summary>
    void RecordConversation(SiteId siteId, DateTimeOffset now);
}
