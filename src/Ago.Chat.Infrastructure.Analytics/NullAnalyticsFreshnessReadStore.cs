using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`: the null-object <see cref="IAnalyticsFreshnessReadStore"/> registered for any host where the
/// <c>ago_analytics</c> rollup pipeline is not configured (no analytics connection string). In that
/// configuration the reports fall back to live <c>ago_chat</c> aggregation (see <c>ChatModule</c>'s own
/// conditional wiring), and live data has no rollup as-of marker - so <c>computedAsOf</c> is honestly
/// <see langword="null"/> ("live / unknown"), never a fabricated timestamp.
///
/// <para>Registered rather than left absent so that <see cref="IAnalyticsFreshnessReadStore"/> always
/// resolves for the analytics handlers (which every serving host registers), whether or not this particular
/// host reads rollups - the same "the port is always resolvable; the implementation varies by host" shape
/// the widget-activity read already uses.</para>
/// </summary>
public sealed class NullAnalyticsFreshnessReadStore : IAnalyticsFreshnessReadStore
{
    public Task<DateTimeOffset?> GetLastRollupCompletedAtAsync(CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(null);
}
