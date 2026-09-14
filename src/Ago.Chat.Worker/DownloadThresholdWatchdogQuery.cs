using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>`25-83`: one soft-threshold crossing still waiting on its own warning mail.</summary>
public sealed record DownloadThresholdWarningCandidate(
    Guid SiteId, string SiteName, string Tier, long BytesOut, long SoftThresholdBytes, long HardThresholdBytes);

/// <summary>
/// `25-83`: the raw SQL half of <see cref="DownloadThresholdWatchdogJob"/> - the identical "Worker
/// job, raw Npgsql, no Application-layer handler" shape <see cref="InactivityWatchdogQuery"/>'s own
/// remarks already establish for a background sweep with no operator behind it, reused here rather
/// than re-derived: a scheduled mail is exactly the same class of thing "who to warn, and when to stop
/// warning them again" is for account inactivity.
/// </summary>
public static class DownloadThresholdWatchdogQuery
{
    /// <summary>A site is a warning candidate the moment its current month's own maintained egress
    /// figure (<c>site_attachment_egress.bytes_out</c>, `23-82`'s own aggregate) reaches its tier's
    /// own soft threshold and has not been notified yet this period
    /// (<c>soft_notified_at is null</c> - reset implicitly every month, this column's own migration
    /// remarks). The inner join to <c>tier_download_thresholds</c> means a tier with no configured row
    /// never produces a candidate at all - the identical "a missing threshold fails open, never
    /// closed" posture <c>IDownloadThresholdReadStore</c>'s own remarks state for the request path,
    /// restated here for the sweep.</summary>
    public static async Task<IReadOnlyList<DownloadThresholdWarningCandidate>> ListSoftThresholdCandidatesAsync(
        NpgsqlConnection connection, DateOnly periodMonth, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            select se.site_id, s.name, s.tier, se.bytes_out, td.soft_threshold_bytes, td.hard_threshold_bytes
            from site_attachment_egress se
            join sites s on s.id = se.site_id
            join tier_download_thresholds td on td.tier = s.tier
            where se.period_month = @periodMonth
              and se.soft_notified_at is null
              and se.bytes_out >= td.soft_threshold_bytes
            order by se.site_id
            limit @limit
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        // Same `DateOnly` -> midnight-UTC-kinded `DateTime` conversion `AttachmentEgressMeterStore`'s
        // own remarks explain - Dapper/Npgsql parameter binding has no built-in `DateOnly` mapping.
        command.Parameters.AddWithValue("periodMonth", periodMonth.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("limit", limit);

        var candidates = new List<DownloadThresholdWarningCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new DownloadThresholdWarningCandidate(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
        }

        return candidates;
    }

    /// <summary>Idempotent, the identical CAS shape <see cref="InactivityWatchdogQuery.MarkWarnedAsync"/>
    /// already gives its own flag: a second call for an already-notified <c>(site, period)</c> updates
    /// nothing, so a candidate list built one moment and acted on the next never double-sends even
    /// under a slow batch.</summary>
    public static async Task<int> MarkSoftNotifiedAsync(
        NpgsqlConnection connection, Guid siteId, DateOnly periodMonth, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            update site_attachment_egress
            set soft_notified_at = @at
            where site_id = @siteId and period_month = @periodMonth and soft_notified_at is null
            """,
            connection);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("siteId", siteId);
        command.Parameters.AddWithValue("periodMonth", periodMonth.ToDateTime(TimeOnly.MinValue));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
