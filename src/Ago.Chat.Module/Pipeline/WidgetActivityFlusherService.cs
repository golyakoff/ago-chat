using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `23-07`: the funnel's own "batch writer" half, timer-driven rather than size-or-delay-driven the
/// way <c>BatchFlusherService</c> is - there is no caller waiting on a count the way
/// <c>ConversationSequencer</c> waits on a message's own flush, so nothing here needs to react to
/// "the batch just got big enough". A plain <see cref="PeriodicTimer"/> tick is the whole mechanism.
///
/// <para><b>A best-effort final flush on shutdown, not a required one.</b> `docs/design/decisions.md`
/// §3 already accepts losing an *unflushed* batch to a pod restart - a hard crash gives this class no
/// chance to run at all - but an ordinary rolling deploy's graceful shutdown does call
/// <see cref="ExecuteAsync"/>'s own cancellation path, and there is no reason to throw away up to
/// <see cref="WidgetActivityOptions.FlushInterval"/> worth of counts when flushing them costs one more
/// upsert and a few milliseconds. Wrapped in its own <c>try</c>/<c>catch</c>: a failure here must
/// never turn an orderly shutdown into a crash loop over a dashboard number.</para>
/// </summary>
public sealed class WidgetActivityFlusherService(
    WidgetActivityAccumulator accumulator,
    WidgetActivityWriter writer,
    IOptions<WidgetActivityOptions> options,
    ILogger<WidgetActivityFlusherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.FlushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown - PeriodicTimer.WaitForNextTickAsync throws this rather than
            // returning false when the token it was given is the one that got cancelled.
        }

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // This type's own doc comment: a best-effort courtesy, never a reason to fail shutdown.
            logger.LogWarning(ex, "Final widget-activity flush on shutdown failed; its counts are lost.");
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var deltas = accumulator.DrainSnapshot();
        if (deltas.Count > 0)
        {
            await writer.FlushAsync(deltas, cancellationToken);
        }
    }
}
