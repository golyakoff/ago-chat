using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `13-03`: the recurring monthly re-charge, and the retry/lapse machinery around a failed one - one
/// tick per subscription whose `current_period_end` has passed or whose `PastDue` retry is due. Same
/// `PeriodicTimer`/`BackgroundService` shape as `AutoCloseInactiveConversationsJob`/`PartitionMaintenanceJob`:
/// runs once immediately, then every <see cref="SubscriptionRenewalJobOptions.Interval"/>, and a
/// transient failure logs and retries next cycle rather than killing the sweep (`concurrency.md`).
///
/// <para><b>A fresh <see cref="IServiceScopeFactory"/> scope per candidate, not per tick.</b> The
/// identical reasoning <see cref="AutoCloseInactiveConversationsJob"/>'s own remarks give: this class is
/// a singleton hosted service, but <see cref="ProcessSubscriptionRenewalHandler"/> and the
/// <see cref="IBillingSubscriptionRepository"/> it (and the candidate-listing call below) depend on are
/// scoped - one shared scope across every candidate in a tick would share one `DbContext` change tracker
/// across every renewal in that tick, silently serving a stale read to the second candidate onward.</para>
/// </summary>
public sealed class SubscriptionRenewalJob(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<SubscriptionRenewalJobOptions> options,
    ILogger<SubscriptionRenewalJob> logger) : BackgroundService
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
                logger.LogError(ex, "Subscription renewal cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    /// <summary><c>internal</c> so an integration test can drive exactly one cycle against a real
    /// Postgres and a fake ЮKassa host instead of waiting for a timer - the same seam
    /// <c>AutoCloseInactiveConversationsJob.RunOnceAsync</c>/<c>DemoTenantExpiryJob.SweepAsync</c>
    /// already expose for the same reason.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        IReadOnlyList<BillingSubscriptionId> due;
        await using (var listScope = scopeFactory.CreateAsyncScope())
        {
            var subscriptions = listScope.ServiceProvider.GetRequiredService<IBillingSubscriptionRepository>();
            due = await subscriptions.ListDueForRenewalAsync(now, options.Value.BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var subscriptionId in due)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ProcessSubscriptionRenewalHandler>();

            try
            {
                var outcome = await handler.HandleAsync(new ProcessSubscriptionRenewal(subscriptionId), cancellationToken);
                LogOutcome(subscriptionId, outcome);
                processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One subscription's failure (a transient ЮKassa/network fault the outbound call's own
                // resilience pipeline could not recover from within a tick) must not stop the others -
                // the same "one candidate's failure logs and moves on" shape
                // `DemoTenantExpiryJob.SweepAsync`'s own remarks establish. This row stays due and is
                // reconsidered next tick.
                logger.LogError(ex, "Failed to process billing subscription {SubscriptionId}; it stays due for the next cycle.", subscriptionId.Value);
            }
        }

        if (processed > 0)
        {
            logger.LogInformation("Processed {Processed} of {Due} due billing subscription(s).", processed, due.Count);
        }
    }

    private void LogOutcome(BillingSubscriptionId subscriptionId, SubscriptionRenewalOutcome outcome)
    {
        switch (outcome)
        {
            case SubscriptionRenewalOutcome.Renewed:
                logger.LogInformation("Billing subscription {SubscriptionId} renewed.", subscriptionId.Value);
                break;
            case SubscriptionRenewalOutcome.Lapsed:
                logger.LogInformation("Billing subscription {SubscriptionId} lapsed; site downgraded to free.", subscriptionId.Value);
                break;
            case SubscriptionRenewalOutcome.ChargeRefused refused:
                logger.LogWarning("Billing subscription {SubscriptionId} renewal charge refused: {Reason}", subscriptionId.Value, refused.Reason);
                break;
            case SubscriptionRenewalOutcome.NotDue:
                logger.LogDebug("Billing subscription {SubscriptionId} was no longer due by the time it was processed.", subscriptionId.Value);
                break;
        }
    }
}
