using System.Diagnostics;
using System.Threading.Channels;
using Ago.Chat.Contracts;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// messaging.md's "Outbox dispatcher": claims unpublished rows with <c>FOR UPDATE SKIP LOCKED</c>,
/// publishes each via <see cref="IEventPublisher"/>, marks <c>published_at</c> on success - safe with
/// multiple <see cref="OutboxDispatcher"/> instances against the same Postgres, since <c>SKIP LOCKED</c>
/// means two instances claiming concurrently simply split the unpublished rows between them rather
/// than racing for the same one (adr/0005). Raw Npgsql, not EF: <c>SELECT ... FOR UPDATE SKIP LOCKED</c>
/// has no LINQ shape, and this dispatcher never needs change tracking.
/// </summary>
public sealed class OutboxDispatcher(
    NpgsqlDataSource dataSource,
    IEventPublisher publisher,
    IClock clock,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const string NotifyChannel = "outbox_new_row";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded to 1 and DropWrite: this is a wake signal, not a queue of individual
        // notifications - many inserts before the dispatcher gets to look just collapse into "there
        // is more work", which is all a batch claim needs to know.
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        await using var listenConnection = await dataSource.OpenConnectionAsync(stoppingToken);
        listenConnection.Notification += (_, _) => wake.Writer.TryWrite(true);
        await using (var listenCommand = new NpgsqlCommand($"LISTEN {NotifyChannel};", listenConnection))
        {
            await listenCommand.ExecuteNonQueryAsync(stoppingToken);
        }

        var notificationPump = PumpNotificationsAsync(listenConnection, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DispatchBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // concurrency.md: a BackgroundService catches and continues - an unobserved
                    // exception here would silently kill the whole dispatch loop.
                    logger.LogError(ex, "Outbox dispatch cycle failed; retrying next cycle.");
                }

                // A fresh Task.Delay every iteration, not a shared PeriodicTimer: PeriodicTimer.
                // WaitForNextTickAsync allows only one in-flight call at a time, and racing it inside
                // Task.WhenAny against the wake signal means the losing call is abandoned mid-flight
                // whenever a notification wins - the next iteration's call to the same timer then
                // violates that single-consumer contract and never completes, permanently stalling
                // the loop after the first notification-driven wakeup (found live: the dispatcher
                // stopped claiming batches entirely, with no exception, no log, nothing - a Postgres
                // NOTIFY had simply out-raced the poll tick once). Task.Delay has no such restriction,
                // so the abandoned loser is simply discarded and garbage-collected.
                var wakeTask = wake.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var pollTask = Task.Delay(options.Value.PollInterval, stoppingToken);
                await Task.WhenAny(wakeTask, pollTask);
                while (wake.Reader.TryRead(out _))
                {
                    // Drain any notifications that arrived while a batch was already in flight.
                }
            }
        }
        finally
        {
            // concurrency.md's shutdown sequence: stop accepting new work, let this cycle finish
            // (already awaited above), then close cleanly - the pump task observes the same
            // stoppingToken and exits on its own.
            await notificationPump;
        }
    }

    private static async Task PumpNotificationsAsync(NpgsqlConnection connection, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Npgsql only raises Notification while something is actively waiting on the
                // connection - this loop is that "actively waiting".
                await connection.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    internal async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var rows = await ClaimBatchAsync(connection, transaction, options.Value.BatchSize, cancellationToken);

        // `7-02`: nfr.md's "outbox lag" - now minus the oldest unpublished row's own occurred_at, as
        // of this cycle. Reused from the batch just claimed (ORDER BY occurred_at ASC, so rows[0] is
        // the oldest of whatever this cycle actually saw) rather than a second, separate query every
        // cycle - an unlocked MIN(occurred_at) would be marginally more precise on a cycle where every
        // truly-oldest row happens to be locked by another replica's own in-flight transaction, but
        // that is a rare, self-correcting edge case (the next cycle picks it up), not worth doubling
        // this loop's own query count for. An empty claim means no lag to report (0), not "unknown" -
        // the same "the gauge is only as fresh as the last dispatch cycle" trade-off ChatMetrics's own
        // remarks document, made explicit here rather than left stale from whatever the last non-empty
        // cycle reported.
        var lagSeconds = rows.Count > 0
            ? (clock.UtcNow - new DateTimeOffset(DateTime.SpecifyKind(rows[0].OccurredAt, DateTimeKind.Utc))).TotalSeconds
            : 0;
        ChatMetrics.SetOutboxLagSeconds(Math.Max(0, lagSeconds));

        foreach (var row in rows)
        {
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishCts.CancelAfter(options.Value.PublishTimeout);

            // `7-01`: messaging.md's "trace id captured at write must survive the poll-and-publish
            // handoff" - a new activity, parented to the context captured at write time (row.
            // TraceContext, OutboxMessage's own remarks), not a fresh root. Ago.Platform.Messaging.
            // RabbitMq's RabbitMqEventPublisher picks up whatever Activity is current right here and
            // injects it as the outgoing `traceparent` header, so this is also the only place that
            // needs to know a parent exists at all - the publisher itself stays generic.
            ChatTracing.TryParseTraceParent(row.TraceContext, out var parentContext);
            using var activity = ChatTracing.Source.StartActivity(ChatTracing.SpanNames.OutboxDispatch, ActivityKind.Producer, parentContext);
            activity?.SetTag("ago.outbox.id", row.Id);
            activity?.SetTag("ago.outbox.type", row.Type);

            try
            {
                await publisher.PublishAsync(row.ToEnvelope(), publishCts.Token);
                await MarkPublishedAsync(connection, transaction, row.Id, clock.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // publishCts fired on its own timeout, not because the dispatcher is shutting down
                // (the outer token is still fine) - an unresponsive broker, treated exactly like any
                // other publish failure: increment attempts, move on to the next row.
                logger.LogWarning("Publishing outbox row {OutboxId} timed out after {Timeout} (attempt {Attempt})",
                    row.Id, options.Value.PublishTimeout, row.Attempts + 1);
                await IncrementAttemptsAsync(connection, transaction, row.Id, cancellationToken);
                ChatMetrics.RecordOutboxPublishFailure();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A lost optimistic-concurrency race is Debug; a broker publish failure is a real,
                // if expected, degraded condition (resilience.md: "outbox accumulates while it is
                // down") - Warning, not Error, since the row is not lost, only delayed.
                logger.LogWarning(ex, "Failed to publish outbox row {OutboxId} (attempt {Attempt})", row.Id, row.Attempts + 1);
                await IncrementAttemptsAsync(connection, transaction, row.Id, cancellationToken);
                ChatMetrics.RecordOutboxPublishFailure();
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ClaimedOutboxRow>> ClaimBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, int batchSize, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, occurred_at, type, version, payload, partition_key, correlation_id, attempts, trace_context
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY occurred_at
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("batchSize", batchSize);

        var rows = new List<ClaimedOutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClaimedOutboxRow(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetGuid(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    private static async Task MarkPublishedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE outbox SET published_at = @publishedAt, attempts = attempts + 1 WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("publishedAt", publishedAt);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task IncrementAttemptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE outbox SET attempts = attempts + 1 WHERE id = @id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
