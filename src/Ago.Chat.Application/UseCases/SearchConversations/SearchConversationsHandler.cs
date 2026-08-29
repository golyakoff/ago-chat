using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SearchConversations;

/// <summary>
/// `18-01`: full-text search across a site's conversations. Gated on
/// <see cref="Permission.SiteConfigure"/>, the same permission
/// <c>GetAllConversationsForSiteHandler</c> uses and for the identical reason: a search over every
/// conversation on the site, not just the caller's own assigned/waiting ones, is the same site-wide
/// oversight capability `authorization.md` reserves for the admin/supervisor role - extending it to
/// every operator holding the ordinary `conversation:read` permission would let anyone read every
/// other operator's conversations by typing a common word, which is exactly the boundary
/// `GetAllConversationsForSiteHandler`'s own remarks already refuse to cross.
///
/// <para><b>The bound decision, made here and nowhere else.</b> `messages` only prunes partitions
/// when a query bounds `created_at` (this item's own Depends-on note: "the ordinary move... silently
/// fails to prune without the date bound"). Rather than reject a search that names no range, this
/// handler defaults it to the trailing <see cref="DefaultWindowMonths"/> months and always echoes the
/// bound actually used back on the response (<see cref="SearchConversationsResponse.SearchedFrom"/>/
/// <see cref="SearchConversationsResponse.SearchedTo"/>) - a truncation the caller cannot see is a
/// search that silently drops results, which `18-01`'s own Done-when calls out by name ("the bound is
/// visible, not silent"). An operator-supplied range is trusted as given and echoed back unchanged;
/// this handler does not additionally cap how wide a caller-chosen range may be - see this item's own
/// commit-prep notes for why a hard ceiling was considered and left out (no measured constraint to
/// hang a number on, `CLAUDE.md`'s ban on invented figures).</para>
///
/// <para><b>Archived history is out of reach through this endpoint, and that is a fact about `13-06`,
/// not about this one.</b> `13-06` (retention archive) has not shipped - nothing is dropped out from
/// under a live search yet beyond `15-04`'s own already-running prune horizon - so today "out of
/// reach" only ever means a range with no matching partition, which behaves as a plain empty result
/// (Postgres range partitioning does not error on a bound with no covering partition, it just finds no
/// rows there). Once `13-06` ships, a period past its horizon is dropped from `messages` entirely and
/// becomes retrievable only through that item's own archive-retrieval flow, as a file - never through
/// this search, which only ever reads the live table.</para>
/// </summary>
public sealed class SearchConversationsHandler(
    IConversationSearchStore searchStore, IPermissionChecker permissions, IClock clock)
{
    /// <summary>Three months, matching the reasoning `Ago.Chat.Worker.MessagePartitionPruneJobOptions.
    /// RetentionHorizonMonths`'s own doc comment already states for this exact table (bounded index
    /// size, comfortably inside what `15-04`'s prune horizon keeps live) - restated here rather than
    /// referenced, since `Ago.Chat.Application` cannot depend on `Ago.Chat.Worker` (clean-architecture.md's
    /// dependency rule: Application depends on Domain only). An operational default, not a
    /// measurement (`CLAUDE.md`), and this is the one place it can drift from that job's own default
    /// without either side knowing - both being three today is a deliberate alignment, not a shared
    /// constant, and there is nothing in the codebase enforcing that they stay equal.</summary>
    public const int DefaultWindowMonths = 3;

    public async Task<Result<SearchConversationsResponse>> HandleAsync(
        SearchConversations query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to search this site's conversations.");
        }

        var phrase = query.Phrase.Trim();
        if (phrase.Length == 0)
        {
            return ConversationErrors.SearchInvalidQuery("Search phrase must not be empty.");
        }

        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddMonths(-DefaultWindowMonths);
        if (from >= to)
        {
            return ConversationErrors.SearchInvalidQuery("The search range's start must be before its end.");
        }

        var page = await searchStore.SearchAsync(
            query.SiteId, phrase, from, to, query.BeforeMessageId, query.PageSize, cancellationToken);

        return new SearchConversationsResponse(
            page.Results.Select(ToDto).ToList(), page.NextBeforeMessageId, from, to);
    }

    private static ConversationSearchResultDto ToDto(ConversationSearchResultItem item) => new(
        item.ConversationId.Value,
        item.MessageId.Value,
        item.Sequence,
        item.MatchedBody,
        item.AuthorKind.ToString(),
        item.CreatedAt,
        item.ConversationState);
}
