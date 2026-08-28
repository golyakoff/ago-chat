using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `18-06`: an `Assigned` conversation nobody has touched inside its per-channel-kind inactivity
/// window closes itself - through the same domain path an operator's own close uses
/// (`AutoCloseConversationHandler`, `Conversation.Close()`, the outbox, `6-09`'s capacity release),
/// never a deletion or an archive (this item's own scope note, restated once more here because it is
/// the fact most likely to be misread from the class name alone). Same
/// `PeriodicTimer`/`BackgroundService` shape as `ConversationAssignmentJob`/`OperatorDisconnectSweepJob`
/// - runs once immediately, then every <see cref="AutoCloseInactiveConversationsJobOptions.Interval"/>,
/// and a transient failure logs and retries next cycle rather than killing the sweep
/// (`concurrency.md`).
///
/// <para><b>Why a fresh <see cref="IServiceScopeFactory"/> scope per conversation, not per tick or
/// per-job.</b> This class is registered as a singleton hosted service (`AddHostedService`'s own
/// lifetime), but <see cref="AutoCloseConversationHandler"/> is scoped - the same
/// `AttachmentThumbnailConsumer`/`UnreadCounterConsumer` shape every other Worker component that needs
/// a scoped Application handler already uses, and for the same reason: a captured scoped instance in a
/// singleton's constructor would share one `DbContext` (and its change tracker) across every close for
/// the life of the process, silently serving stale reads to the second candidate onward. One scope per
/// candidate keeps every close exactly as isolated as a real operator's own `CloseConversationHandler`
/// call already is.</para>
/// </summary>
public sealed class AutoCloseInactiveConversationsJob(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<AutoCloseInactiveConversationsJobOptions> options,
    ILogger<AutoCloseInactiveConversationsJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres blip
                // here must not permanently kill the auto-close sweep.
                logger.LogError(ex, "Auto-close cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary>One pass over every channel kind plus the widget bucket, each with its own cutoff -
    /// the per-channel-kind window this item exists to prove (`AutoCloseInactiveConversationsQuery`'s
    /// own remarks on why that is two SQL shapes rather than one parameterised by a runtime `CASE`).
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await CloseStaleBatchAsync(channelKind: null, now - options.Value.WidgetInactivityWindow, cancellationToken);
        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            await CloseStaleBatchAsync(kind, now - options.Value.WindowFor(kind), cancellationToken);
        }
    }

    private async Task CloseStaleBatchAsync(ChannelKind? channelKind, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationId> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync(
                connection, channelKind, cutoff, options.Value.BatchSize, cancellationToken);
        }

        var channelTag = channelKind?.ToString() ?? "widget";
        var closedCount = 0;
        foreach (var conversationId in candidates)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var autoClose = scope.ServiceProvider.GetRequiredService<AutoCloseConversationHandler>();
            var result = await autoClose.HandleAsync(new AutoCloseConversation(conversationId), cancellationToken);

            if (result.IsSuccess)
            {
                closedCount++;
                ChatMetrics.RecordConversationAutoClosed(channelTag);
            }
            else
            {
                // A candidate that no longer qualifies by the time it is actually closed - a message
                // arrived, an operator closed it themselves, or `4-04`'s disconnect release moved it
                // back to Waiting, all between the scan above and this call. Not an error: the same
                // "normal outcome to retry, not an error" shape `concurrency.md` already gives
                // IOperatorCapacity.TryClaimAsync losing its own race. Left for the next cycle to
                // re-evaluate against fresh data rather than retried immediately here.
                logger.LogDebug(
                    "Auto-close skipped for conversation {ConversationId}: {ErrorCode}.",
                    conversationId.Value, result.Error!.Value.Code);
            }
        }

        if (closedCount > 0)
        {
            // `18-06`'s own Done-when: a log line distinguishable from an operator-initiated close.
            // ChatMetrics.RecordConversationAutoClosed above is the metric half of the same
            // requirement.
            logger.LogInformation(
                "Auto-closed {Count} inactive {ChannelKind} conversation(s) past their {Cutoff:O} inactivity cutoff.",
                closedCount, channelTag, cutoff);
        }
    }
}
