using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListSitesForOwner;

/// <summary>
/// `12-02`: assembles the platform owner's cross-tenant overview - decides the recent-activity
/// window and the page size, then hands both to <see cref="IPlatformOverviewReadStore"/> and maps
/// rows to the wire shape. Read-only: it opens no transaction, writes nothing, and publishes nothing.
///
/// <para><b>This handler performs no authorization check, and that is deliberate rather than an
/// omission.</b> Its sibling `GetAllConversationsForSiteHandler` calls
/// <c>IPermissionChecker</c> because the fact it needs - does this operator hold `site:configure` for
/// this site - lives in tables this codebase owns. The fact that authorizes *this* call does not: it
/// is a `platform-owner` realm role Keycloak signs into the token (`adr/0032`), and Application has
/// no port that can see a claim (nor should it - claims are a transport concern, and inventing an
/// `ICurrentPrincipal` port to re-check at this layer what the policy already decided would be a
/// second, weaker copy of the same rule, drifting from the first the moment either changes). The
/// single gate is `12-01`'s `RequirePlatformOwner` policy on `GET /api/v1/owner/sites`
/// (`OwnerSitesEndpoints`), which is the only route that resolves this handler.</para>
/// </summary>
public sealed class ListSitesForOwnerHandler(IPlatformOverviewReadStore readStore, IClock clock)
{
    /// <summary>The one tier that exists (`10-02`: "no tier/plan column anywhere. There is exactly
    /// one tier today (free)"). A constant here, not a column read and not a computation over usage -
    /// `OwnerSiteSummaryDto.Tier` states in full why a constant is the honest answer today and why
    /// the field exists anyway.</summary>
    internal const string OnlyTier = "free";

    /// <summary>How far back <c>recentMessageCount</c>/<c>lastMessageAt</c> look.
    ///
    /// <para><b>30 days, and why that number is a choice and not a measurement:</b> `messages` is
    /// partitioned monthly (`2-06`), so a 30-day window spans at most two partitions no matter which
    /// day of the month the query runs - the smallest bound that still answers "is this tenant
    /// active" over a full billing-shaped month. It is not tuned against any load test and no
    /// performance claim rests on it (`CLAUDE.md`: measure or stay silent); what is load-bearing is
    /// that the window is *bounded at all*, which is what keeps the query off every historical
    /// partition. A different length would be equally defensible and needs no code change beyond this
    /// constant - the port takes the resulting timestamp, not a policy.</para>
    ///
    /// <para><c>public</c> because it is already public on the wire: every response carries it
    /// (<see cref="OwnerSitesResponse.RecentWindowDays"/>), so a caller that needs to name the same
    /// number should read this rather than restate `30` and drift from it.</para></summary>
    public const int RecentWindowDays = 30;

    /// <summary>Page size when the caller names none, and the ceiling when they name a large one.
    /// Both are deliberate, unmeasured bounds - a caller asking for a million rows should get a page,
    /// not an out-of-memory - not tuned figures, and nothing claims they are optimal.</summary>
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<OwnerSitesResponse> HandleAsync(ListSitesForOwner query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        // From IClock, never DateTimeOffset.UtcNow (`CLAUDE.md` rule 11) - which is also what makes
        // the window testable at a fixed instant instead of "whatever the machine said".
        var recentSince = clock.UtcNow.AddDays(-RecentWindowDays);

        var page = await readStore.ListSitesAsync(recentSince, query.Before, limit, cancellationToken);

        return new OwnerSitesResponse(
            page.Sites.Select(ToSummary).ToList(), page.NextBefore, RecentWindowDays);
    }

    private static OwnerSiteSummaryDto ToSummary(SiteOverviewItem item) => new(
        item.Id.Value,
        item.Name,
        OnlyTier,
        item.CreatedAt,
        item.SeatCount,
        item.ConversationCount,
        item.RecentMessageCount,
        item.LastMessageAt,
        item.AttachmentBytes);
}
