using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Application.UseCases.ReleaseInactiveConversation;
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

    /// <summary>`25-118`: the widget bucket is now two passes, not one - see
    /// `AutoCloseInactiveConversationsJobOptions.WidgetCloseWindow`'s own remarks and the backlog
    /// item's "Answered" design for why. <b>Release runs before close, deliberately</b>: an
    /// `Assigned` conversation just past `WidgetInactivityWindow` but nowhere near
    /// `WidgetCloseWindow` must end this tick in `Waiting` (released), never skip straight to
    /// `Closed` - running close first, against a query that also matches `Assigned` rows, would let a
    /// conversation whose `WidgetCloseWindow` cutoff is (rarely, but possibly, given
    /// `AutoCloseInactiveConversationsJobOptions.Interval`'s own cadence relative to a misconfigured,
    /// very short `WidgetCloseWindow`) already in the past be closed directly from `Assigned` without
    /// ever visibly passing through the release step; running release first makes the two passes
    /// compose the way the design intends regardless of how far apart the two windows are configured.
    /// The channel-kind loop below is the per-channel-kind window this item exists to prove
    /// (`AutoCloseInactiveConversationsQuery`'s own remarks on why that is two SQL shapes rather than
    /// one parameterised by a runtime `CASE`) - entirely unchanged by this item.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        await ReleaseStaleAssignedWidgetBatchAsync(now - options.Value.WidgetInactivityWindow, cancellationToken);
        await CloseStaleWidgetBatchAsync(now - options.Value.WidgetCloseWindow, cancellationToken);

        foreach (var kind in Enum.GetValues<ChannelKind>())
        {
            await CloseStaleBatchAsync(kind, now - options.Value.WindowFor(kind), cancellationToken);
        }
    }

    /// <summary>`25-118`'s new release pass: reuses `AutoCloseInactiveConversationsQuery`'s existing
    /// widget-Assigned-only scan unchanged (the same one this job used to feed straight to
    /// <see cref="AutoCloseConversationHandler"/>), but hands each candidate to
    /// <see cref="ReleaseInactiveConversationHandler"/> instead - <see cref="Conversation.ReleaseToQueue"/>
    /// rather than <see cref="Conversation.Close"/>, so the operator's capacity slot still frees
    /// immediately but the conversation itself stays resumable.</summary>
    private async Task ReleaseStaleAssignedWidgetBatchAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationId> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync(
                connection, channelKind: null, cutoff, options.Value.BatchSize, cancellationToken);
        }

        var releasedCount = 0;
        foreach (var conversationId in candidates)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var release = scope.ServiceProvider.GetRequiredService<ReleaseInactiveConversationHandler>();
            var result = await release.HandleAsync(new ReleaseInactiveConversation(conversationId), cancellationToken);

            if (result.IsSuccess)
            {
                releasedCount++;
                ChatMetrics.RecordConversationAutoReleased();
            }
            else
            {
                // The identical "no longer qualifies by the time it is actually handled" outcome
                // CloseStaleBatchAsync's own remarks describe - not an error, left for the next cycle.
                logger.LogDebug(
                    "Auto-release skipped for conversation {ConversationId}: {ErrorCode}.",
                    conversationId.Value, result.Error!.Value.Code);
            }
        }

        if (releasedCount > 0)
        {
            logger.LogInformation(
                "Auto-released {Count} inactive widget conversation(s) back to Waiting past their {Cutoff:O} inactivity cutoff.",
                releasedCount, cutoff);
        }
    }

    /// <summary>`25-118`'s new close pass: the widget-only counterpart to <see cref="CloseStaleBatchAsync"/>
    /// below, sharing its per-candidate close logic (<see cref="RunAutoCloseHandlerBatchAsync"/>) but
    /// sourcing candidates from <see cref="AutoCloseInactiveConversationsQuery.FindStaleWidgetBatchIncludingWaitingAsync"/>
    /// - the query variant that also reaches `Waiting` rows, which is what actually lets a conversation
    /// this same job already released (above) eventually close once genuinely abandoned.</summary>
    private async Task CloseStaleWidgetBatchAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationId> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await AutoCloseInactiveConversationsQuery.FindStaleWidgetBatchIncludingWaitingAsync(
                connection, cutoff, options.Value.BatchSize, cancellationToken);
        }

        var closedCount = await RunAutoCloseHandlerBatchAsync(candidates, "widget", cancellationToken);
        if (closedCount > 0)
        {
            logger.LogInformation(
                "Auto-closed {Count} inactive widget conversation(s) (Assigned or Waiting) past their {Cutoff:O} inactivity cutoff.",
                closedCount, cutoff);
        }
    }

    /// <summary>The channel-kind pass - `18-06`'s own original shape, untouched by `25-118`: still one
    /// window, still `Assigned`-only, still <see cref="AutoCloseConversationHandler"/>.</summary>
    private async Task CloseStaleBatchAsync(ChannelKind channelKind, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationId> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await AutoCloseInactiveConversationsQuery.FindStaleAssignedBatchAsync(
                connection, channelKind, cutoff, options.Value.BatchSize, cancellationToken);
        }

        var channelTag = channelKind.ToString();
        var closedCount = await RunAutoCloseHandlerBatchAsync(candidates, channelTag, cancellationToken);
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

    /// <summary>`25-118`: factored out of `CloseStaleBatchAsync` so the new widget close pass
    /// (<see cref="CloseStaleWidgetBatchAsync"/>) and the unchanged channel-kind pass share the exact
    /// same per-candidate close logic - same fresh-scope-per-candidate shape, same skip-not-error
    /// handling, same metric - rather than two copies that could drift.</summary>
    private async Task<int> RunAutoCloseHandlerBatchAsync(
        IReadOnlyList<ConversationId> candidates, string channelTag, CancellationToken cancellationToken)
    {
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
                // arrived, an operator closed it themselves, or `4-04`'s disconnect release (or,
                // `25-118` onward, this job's own release pass above) moved it back to Waiting, all
                // between the scan above and this call. Not an error: the same "normal outcome to
                // retry, not an error" shape `concurrency.md` already gives
                // IOperatorCapacity.TryClaimAsync losing its own race. Left for the next cycle to
                // re-evaluate against fresh data rather than retried immediately here.
                logger.LogDebug(
                    "Auto-close skipped for conversation {ConversationId}: {ErrorCode}.",
                    conversationId.Value, result.Error!.Value.Code);
            }
        }

        return closedCount;
    }
}
