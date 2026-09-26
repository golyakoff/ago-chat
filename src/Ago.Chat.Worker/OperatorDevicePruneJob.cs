using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `26-123`/`adr/0185`: the fourth `operator_devices` revocation cause `adr/0179` §1 originally said
/// would never exist - a bounded, timer-based prune of a row whose <see cref="Domain.OperatorDevice.LastSeenAt"/>
/// has not moved in <see cref="OperatorDevicePruneJobOptions.Threshold"/>, the backstop for the one gap
/// the other three causes cannot reach: `26-83`/`26-122` found live that RuStore answers `200 OK` for a
/// send to some tokens a reinstall leaves behind, so "the provider says the token is gone"
/// (`push-notifications.md`'s own outcome table) structurally never fires for them and the row would
/// otherwise live forever.
///
/// Same <c>BackgroundService</c>/<c>PeriodicTimer</c> shape, and the same bounded-batch-per-cycle loop,
/// as <see cref="AccessRecordPruneJob"/>/<see cref="OutboxPruneJob"/> (`concurrency.md`, and this
/// codebase's own convention: reuse the shape, don't invent a second one) - a transient failure logs
/// and retries next cycle rather than killing the sweep, and one cycle's total work is bounded on both
/// axes (<see cref="OperatorDevicePruneJobOptions.BatchSize"/> per statement,
/// <see cref="OperatorDevicePruneJobOptions.MaxBatchesPerCycle"/> per cycle).
///
/// <para><b>Revokes, never deletes and never sends.</b> The query
/// (<see cref="OperatorDevicePruneQuery.RevokeStaleBatchAsync"/>) is a plain SQL `UPDATE` setting
/// `revoked_at`, the identical "revoke, don't erase" choice every other cause on this row already makes
/// - this job needs no `OperatorDevice`/EF round trip at all, because a bulk timestamp set is exactly
/// what <see cref="Domain.OperatorDevice.Revoke"/> already reduces to, and going through EF per row
/// would cost one round trip per stale device for no behavioural difference. No push is sent by this
/// job (`26-123`'s own scope) - it only revokes.</para>
/// </summary>
public sealed class OperatorDevicePruneJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<OperatorDevicePruneJobOptions> options,
    ILogger<OperatorDevicePruneJob> logger) : BackgroundService
{
    private const string TableTag = "operator_devices";
    private const string RevokeCause = "stale_timeout";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres blip
                // here must not permanently kill this safety net.
                logger.LogError(ex, "Operator device prune cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary>One bounded pass. <c>internal</c> so an integration test can drive exactly one cycle
    /// against a real Postgres instead of waiting for a timer - the same seam
    /// <see cref="AccessRecordPruneJob.PruneAsync"/>/<see cref="OutboxPruneJob.PruneAsync"/> already
    /// expose for the same reason.</summary>
    internal async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var cutoff = startedAt - options.Value.Threshold;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var totalRevoked = 0;
        for (var batch = 0; batch < options.Value.MaxBatchesPerCycle; batch++)
        {
            var revoked = await OperatorDevicePruneQuery.RevokeStaleBatchAsync(
                connection, startedAt, cutoff, options.Value.BatchSize, cancellationToken);
            totalRevoked += revoked;

            if (revoked < options.Value.BatchSize)
            {
                // Fewer rows than requested means this was the last batch - caught up, no point
                // issuing another statement that would revoke zero.
                break;
            }
        }

        if (totalRevoked > 0)
        {
            logger.LogInformation(
                "Operator device prune revoked {Count} stale device registration(s) with no activity since before {Cutoff:O}.",
                totalRevoked, cutoff);
            ChatMetrics.RecordPushTokenRevoked(RevokeCause, totalRevoked);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, totalRevoked, clock.UtcNow - startedAt);
        return totalRevoked;
    }
}
