using Ago.Chat.Application.Abstractions;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-83`'s <see cref="IDownloadThresholdReadStore"/> - a plain Dapper read against
/// <c>tier_download_thresholds</c> (see that table's own migration remarks,
/// <c>Stage25AddTierDownloadThresholds</c>), no different in shape from
/// <see cref="AttachmentEgressReadStore"/> right beside it.</summary>
public sealed class DownloadThresholdReadStore(NpgsqlDataSource dataSource) : IDownloadThresholdReadStore
{
    private const string Sql = """
        SELECT soft_threshold_bytes AS "SoftThresholdBytes", hard_threshold_bytes AS "HardThresholdBytes"
        FROM tier_download_thresholds
        WHERE tier = @Tier
        """;

    public async Task<DownloadThresholds> GetForTierAsync(string tier, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ThresholdRow?>(new CommandDefinition(
            Sql, new { Tier = tier }, cancellationToken: cancellationToken));

        // This port's own remarks: no row for this tier fails open, never closed.
        return row is null
            ? DownloadThresholds.Unbounded(tier)
            : new DownloadThresholds(tier, row.SoftThresholdBytes, row.HardThresholdBytes);
    }

    private sealed record ThresholdRow(long SoftThresholdBytes, long HardThresholdBytes);
}
