using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-84`: <see cref="IDownloadOverageReadStore"/> - two Dapper aggregate reads over
/// <c>download_overage_charges</c>, no different in shape from <see cref="AttachmentEgressReadStore"/>
/// beside it (adr/0004: writes through an adapter, reads through Dapper).</summary>
public sealed class DownloadOverageReadStore(NpgsqlDataSource dataSource) : IDownloadOverageReadStore
{
    private const string SettlementSql = """
        SELECT
            COALESCE(SUM(bytes_over), 0)::bigint AS "SettledBytes",
            COALESCE(SUM(amount_rub), 0)::numeric(12,2) AS "SettledAmountRub",
            COALESCE(bool_or(source = 'Checkout'), false) AS "HasPaidCheckout"
        FROM download_overage_charges
        WHERE site_id = @SiteId AND period_month = @PeriodMonth AND status = 'Succeeded'
        """;

    // `site_attachment_egress` is the left side: a month with no egress row has no overage by
    // definition, and a charge row can never exist without one. LEFT JOIN onto the already-settled
    // total so a month nobody has paid for yet still appears with its full outstanding balance.
    private const string OutstandingSql = """
        SELECT
            e.period_month AS "PeriodMonth",
            (e.bytes_out - @HardThresholdBytes - COALESCE(c.settled_bytes, 0))::bigint AS "OutstandingBytes",
            COALESCE(c.has_paid_checkout, false) AS "HasPaidCheckout"
        FROM site_attachment_egress e
        LEFT JOIN (
            SELECT site_id, period_month, SUM(bytes_over) AS settled_bytes,
                   bool_or(source = 'Checkout') AS has_paid_checkout
            FROM download_overage_charges
            WHERE status = 'Succeeded'
            GROUP BY site_id, period_month
        ) c ON c.site_id = e.site_id AND c.period_month = e.period_month
        WHERE e.site_id = @SiteId
          AND e.period_month <= @UpToPeriodMonth
          AND e.bytes_out - @HardThresholdBytes - COALESCE(c.settled_bytes, 0) > 0
        ORDER BY e.period_month
        """;

    public async Task<DownloadOverageSettlement> GetSettlementAsync(
        SiteId siteId, DateOnly periodMonth, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // `DateTime`, not the `DateOnly` this method was handed - Dapper has no built-in parameter
        // mapping for it, the identical conversion `AttachmentEgressReadStore`'s own remarks already
        // state for the table this one joins against. (Found by this item's own real-Postgres pass: a
        // `DateOnly` parameter throws `NotSupportedException` at Dapper's parameter generator, which no
        // amount of mocked `Application.Tests` coverage could have reached.)
        var row = await connection.QuerySingleOrDefaultAsync<DownloadOverageSettlement>(new CommandDefinition(
            SettlementSql,
            new { SiteId = siteId.Value, PeriodMonth = periodMonth.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken));

        // An aggregate query always returns one row, so this is belt-and-braces rather than a real
        // branch - but "no rows" and "a row of zeros" mean the identical thing to every caller, which
        // is exactly what this port's own remarks promise.
        return row ?? DownloadOverageSettlement.None;
    }



    public async Task<IReadOnlyList<DownloadOverageOutstanding>> GetOutstandingAsync(
        SiteId siteId, long hardThresholdBytes, DateOnly upToPeriodMonth, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<OutstandingRow>(new CommandDefinition(
            OutstandingSql,
            new
            {
                SiteId = siteId.Value,
                HardThresholdBytes = hardThresholdBytes,
                // See GetSettlementAsync's own remarks - Dapper cannot bind a `DateOnly` parameter.
                UpToPeriodMonth = upToPeriodMonth.ToDateTime(TimeOnly.MinValue),
            },
            cancellationToken: cancellationToken));

        return rows
            .Select(row => new DownloadOverageOutstanding(row.PeriodMonth, row.OutstandingBytes, row.HasPaidCheckout))
            .ToList();
    }

    /// <summary>The wire shape of <see cref="OutstandingSql"/>'s own rows. <b>Note the asymmetry, which
    /// this item's own real-Postgres pass established rather than assumed</b>: a <c>date</c> column
    /// <em>reads back</em> as a <see cref="DateOnly"/> (Npgsql maps it natively), but a
    /// <see cref="DateOnly"/> cannot be <em>bound as a parameter</em> - Dapper has no parameter mapping
    /// for it and throws. So the parameters above convert and this type does not.</summary>
    private sealed record OutstandingRow(DateOnly PeriodMonth, long OutstandingBytes, bool HasPaidCheckout);
}
