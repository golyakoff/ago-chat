using Npgsql;

namespace Ago.Chat.Infrastructure.Analytics;

/// <summary>
/// `26-223`/`adr/0186` §3.2: the connection pool for the <c>ago_analytics</c> rollup database, wrapped in
/// a dedicated type rather than registered as a bare <see cref="NpgsqlDataSource"/> for one load-bearing
/// reason - <c>Ago.Chat.Infrastructure.Postgres</c> already registers a singleton <see cref="NpgsqlDataSource"/>
/// for the operational <c>ago_chat</c> database, and the two must never be confused at a DI resolution:
/// they point at different databases, with different credentials and different pools (that separation is
/// the whole point of the split - `ago_analytics` has its own pool so it never competes for `ago_chat`'s).
/// A distinct wrapper type makes "which data source is this" a compile-time fact, not a registration-order
/// accident.
///
/// <para>Relocating the rollups later ("different database now, different host later", design §3.2) is a
/// change to this one connection string in the host's configuration - no code change here.</para>
/// </summary>
public sealed class AnalyticsDbDataSource(NpgsqlDataSource dataSource) : IDisposable, IAsyncDisposable
{
    /// <summary>The underlying pool. Read-only use only (rule 8 / adr/0186): the report reads issue
    /// grouped range scans over the rollup tables and never a write, and never a cross-database join back
    /// to <c>ago_chat</c>.</summary>
    public NpgsqlDataSource Value { get; } = dataSource;

    public void Dispose() => Value.Dispose();

    public ValueTask DisposeAsync() => Value.DisposeAsync();
}
