using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-82`'s <see cref="IAttachmentEgressReadStore"/> - the Dapper read half of
/// <see cref="AttachmentEgressMeterStore"/>'s own aggregate (adr/0004).</summary>
public sealed class AttachmentEgressReadStore(NpgsqlDataSource dataSource) : IAttachmentEgressReadStore
{
    private const string Sql = """
        SELECT download_count AS "DownloadCount", bytes_out AS "BytesOut"
        FROM site_attachment_egress
        WHERE site_id = @SiteId AND period_month = @PeriodMonth
        """;

    public async Task<SiteAttachmentEgress> GetForSiteAsync(
        SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // See AttachmentEgressMeterStore's own remarks on why this is `DateTime`, not the `DateOnly`
        // this method was handed - Dapper has no built-in parameter mapping for it.
        var row = await connection.QuerySingleOrDefaultAsync<EgressRow?>(new CommandDefinition(
            Sql,
            new { SiteId = siteId.Value, PeriodMonth = periodMonth.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));

        // `23-82`'s own read port remarks: "no evidence" and "measured zero" are the same fact for a
        // tenant nobody has downloaded from this month, so a missing row becomes an honest zero here
        // rather than a null the caller has to special-case.
        return row is null
            ? new SiteAttachmentEgress(siteId, periodMonth, 0, 0)
            : new SiteAttachmentEgress(siteId, periodMonth, row.DownloadCount, row.BytesOut);
    }

    private sealed record EgressRow(long DownloadCount, long BytesOut);
}
