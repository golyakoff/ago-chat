using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-01`: full-text search over `messages`, scoped to one site and one date range - its own port
/// rather than a method on <see cref="IConversationReadStore"/>, because the shape is genuinely
/// different (a phrase and a mandatory bound, not a conversation id to page through) and because the
/// tenant scope is carried differently: every other method in <see cref="IConversationReadStore"/>
/// that touches `messages` reaches the site only through `conversation_id` (that interface's own
/// remarks explain why), while this one filters `messages.site_id` directly - the whole reason
/// `adr/0031`'s Addendum denormalized the column in the first place.
///
/// <para><b><paramref name="from"/>/<paramref name="to"/> are mandatory, not optional.</b> This is
/// the item's own scope decision, not an incidental parameter: `messages` prunes only when a query
/// bounds `created_at`, and `18-01`'s Depends-on note names exactly this as the trap - "the ordinary
/// move (just add a GIN index and call it done) silently fails to prune without the date bound." The
/// caller (<c>SearchConversationsHandler</c>) is where the bound is decided and defaulted when the
/// operator did not supply one; this port never invents a default of its own, so the two cannot drift
/// apart.</para>
/// </summary>
public interface IConversationSearchStore
{
    Task<ConversationSearchPage> SearchAsync(
        SiteId siteId,
        string phrase,
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? beforeMessageId,
        int pageSize,
        CancellationToken cancellationToken);
}
