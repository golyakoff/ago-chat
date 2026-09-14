using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-82`'s <see cref="IAttachmentEgressMeter"/> - a plain upsert against
/// <c>site_attachment_egress</c> (see that table's own migration remarks,
/// <c>Stage23AddSiteAttachmentEgress</c>), through <see cref="NpgsqlDataSource"/> directly rather than
/// <c>AgoChatDbContext</c>. Deliberately not participating in any ambient EF transaction, unlike
/// <see cref="SiteAttachmentStorageBudgetStore"/>'s own raw SQL: that store's reservation must commit
/// or roll back with the very presign/row it gates (a write decision, CLAUDE.md rule 8); this one is
/// recorded strictly after a download URL has already been handed out, by a handler that opens no
/// transaction of its own - see <c>GetAttachmentDownloadUrlHandler.RecordDownloadAsync</c>'s own
/// remarks for why its failure is caught and logged rather than allowed to fail the request that
/// triggered it.
/// </summary>
public sealed class AttachmentEgressMeterStore(NpgsqlDataSource dataSource) : IAttachmentEgressMeter
{
    // `ON CONFLICT ... DO UPDATE`, not a read-then-write: two downloads of the same site in the same
    // month racing each other must both land, the same "one statement, not a lost-update window"
    // property every other maintained counter in this codebase already has
    // (SiteAttachmentStorageBudgetStore's own CTE, OperatorCapacityStore's ExecuteSqlInterpolatedAsync).
    private const string Sql = """
        INSERT INTO site_attachment_egress (site_id, period_month, download_count, bytes_out)
        VALUES (@SiteId, @PeriodMonth, 1, @Bytes)
        ON CONFLICT (site_id, period_month)
        DO UPDATE SET
            download_count = site_attachment_egress.download_count + 1,
            bytes_out = site_attachment_egress.bytes_out + @Bytes
        """;

    public async Task RecordAsync(SiteId siteId, DateOnly periodMonth, long bytes, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // `PeriodMonth = periodMonth.ToDateTime(...)`, not the `DateOnly` itself - Dapper has no
        // built-in parameter mapping for `DateOnly` (it throws `NotSupportedException` rather than
        // silently doing the wrong thing, found by running the real fault-injection test against a
        // real Postgres). A midnight-UTC-kinded `DateTime` is what Npgsql's own `date` mapping expects
        // from ADO.NET callers that predate `DateOnly`.
        await connection.ExecuteAsync(new CommandDefinition(
            Sql,
            new { SiteId = siteId.Value, PeriodMonth = periodMonth.ToDateTime(TimeOnly.MinValue), Bytes = bytes },
            cancellationToken: cancellationToken));
    }
}
