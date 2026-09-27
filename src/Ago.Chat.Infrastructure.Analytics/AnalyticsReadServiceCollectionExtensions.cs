using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` §8: wires the precomputed-rollup analytics reads onto the dedicated
/// <c>ago_analytics</c> database. The one place that knows analytics reads are Dapper-over-a-second-Postgres
/// (clean-architecture.md: the Infrastructure adapter owns the connection wiring), called from
/// <c>ChatModule</c> only when an analytics connection string is configured.
/// </summary>
public static class AnalyticsReadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <c>ago_analytics</c> connection pool and swaps the analytics read ports to their
    /// rollup-backed implementations:
    /// <list type="bullet">
    /// <item><see cref="IOperatorAnalyticsReadStore"/> is <b>replaced</b> - <c>AddPostgresPersistence</c>
    /// already registered the live <c>ago_chat</c> implementation as the default/fallback, and this
    /// overrides it wherever the rollup pipeline exists (the config-guarded fallback: fast rollup reads in
    /// production, correct live reads anywhere the pipeline is absent).</item>
    /// <item><see cref="IAnalyticsFreshnessReadStore"/> is registered as the real, rollup-backed store (the
    /// null-object default is registered by <c>ChatModule</c> only in the no-analytics branch).</item>
    /// </list>
    /// <paramref name="analyticsConnectionString"/> points at <c>ago_analytics</c> (its own credentials,
    /// its own pool) - never the operational <c>ago_chat</c> connection.
    /// </summary>
    public static IServiceCollection AddAnalyticsRollupReads(
        this IServiceCollection services, string analyticsConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analyticsConnectionString);

        // Eagerly built once for the process, wrapped in the dedicated type so it never collides with the
        // operational NpgsqlDataSource singleton (the same eager `NpgsqlDataSourceBuilder(...).Build()`
        // shape AddPostgresPersistence uses for ago_chat).
        var dataSource = new NpgsqlDataSourceBuilder(analyticsConnectionString).Build();
        services.AddSingleton(new AnalyticsDbDataSource(dataSource));

        services.Replace(ServiceDescriptor.Scoped<IOperatorAnalyticsReadStore, RollupOperatorAnalyticsReadStore>());
        services.Replace(ServiceDescriptor.Scoped<IAnalyticsFreshnessReadStore, AnalyticsFreshnessReadStore>());

        // `26-237`: the conversion and tag-breakdown reads join site-analytics on the rollups - the same
        // config-guarded Replace, so a host with the ago_analytics pipeline gets the fast O(days) rollup
        // read and any host without it keeps the correct live ago_chat read behind the identical unchanged
        // port. Both rollup stores resolve their id-keyed display names in the application layer through
        // IAnalyticsLabelReadStore (design §8.1), never a cross-database join.
        services.Replace(ServiceDescriptor.Scoped<IConversionReportReadStore, RollupConversionReportReadStore>());
        services.Replace(ServiceDescriptor.Scoped<ITagBreakdownReadStore, RollupTagBreakdownReadStore>());

        // `26-237` (operator-load rollup, decision B): the last analytics read still computed live over
        // ago_chat - the O(N²) correlated-overlap scan of conversation_assignments that is the «По сайту»
        // 499's root cause - moves onto the rollup here, the same config-guarded Replace. The overlap is
        // resolved once at interval close (ConversationAssignmentLog emits ConversationAssignmentClosed);
        // this read is a plain grouped sum and folds the exact concurrent-load rows into AnalyticsOptions'
        // configured buckets in C#. Operator display names are resolved in the handler via
        // IAnalyticsLabelReadStore, exactly like the site-analytics report's own operator rows.
        services.Replace(ServiceDescriptor.Scoped<IOperatorLoadReportReadStore, RollupOperatorLoadReportReadStore>());

        return services;
    }
}
