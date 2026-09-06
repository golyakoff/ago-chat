using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-07`: the read side of the funnel - `adr/0004`'s Dapper read-side rule, the identical shape
/// <see cref="IConversationReadStore"/> already takes for an admin-facing aggregate read.
/// <c>GetSiteInstallationHandler</c> is this port's only caller, and it is a low-frequency operator
/// read (the same "not wrapped in `ICache.GetOrCreateAsync`" reasoning that handler's own remarks give
/// for the other two facts it already reads uncached) - a stale funnel number on the one screen that
/// exists to tell a tenant the truth about their own install would be the exact defect `23-06` closed
/// elsewhere reappearing here.
/// </summary>
public interface IWidgetActivityReadStore
{
    /// <summary>The three counts summed over every day from <paramref name="since"/> (inclusive)
    /// through today, UTC. A brand-new site with no rows at all reads back as
    /// <see cref="WidgetActivityTotals.None"/>, not a missing record - the same "no row" hazard
    /// <see cref="ISiteInstallationSignalRepository.GetAsync"/>'s own remarks describe for its table.
    /// </summary>
    Task<WidgetActivityTotals> GetTotalsAsync(SiteId siteId, DateOnly since, CancellationToken cancellationToken);
}

/// <summary>The funnel's three numbers for one site over one window, carried as one value because
/// <see cref="Domain.WidgetFunnelAdviceResolver.Resolve"/> always reasons about all three together.
/// </summary>
public sealed record WidgetActivityTotals(int Loads, int Opens, int Conversations)
{
    public static readonly WidgetActivityTotals None = new(0, 0, 0);
}
