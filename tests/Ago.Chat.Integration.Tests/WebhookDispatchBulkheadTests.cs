using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;
using Ago.Chat.FakeCrm.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s per-tenant bulkhead Done-when: many endpoints across two sites, one site's endpoints all
/// pointed at a real `Ago.Chat.FakeCrm` process configured to hang - the *other* site's deliveries,
/// dispatched concurrently against the same shared <see cref="Ago.Chat.Webhooks.WebhookResiliencePipelines"/>
/// instance, are not measurably slowed. Proven by measuring each site's own dispatch latency
/// independently while both run at the same time, not by asserting the config alone.
///
/// Uses <see cref="WebhookDispatchTestHarness.BuildConsumerServices"/> for the same reason
/// <see cref="WebhookDispatchBreakerTests"/> does - the two concurrent dispatches below each need their
/// own <c>AgoChatDbContext</c>, not one shared, non-thread-safe instance.
/// </summary>
[Collection(WebhookDispatchCollection.Name)]
public sealed class WebhookDispatchBulkheadTests(WebhookDispatchFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneSitesEndpointsAllHang_TheOtherSitesDeliveriesAreNotMeasurablySlowed()
    {
        await using var hangingCrm = new FakeCrmProcessFixture { DefaultBehavior = "hang" };
        await using var succeedingCrm = new FakeCrmProcessFixture { DefaultBehavior = "succeeds" };
        await Task.WhenAll(hangingCrm.InitializeAsync(), succeedingCrm.InitializeAsync());

        await using var seedDb = fixture.CreateDbContext();
        var hangingSite = await WebhookDispatchTestHarness.SeedSiteAsync(seedDb);
        var healthySite = await WebhookDispatchTestHarness.SeedSiteAsync(seedDb);

        // "many endpoints" for the hanging tenant - each one occupies a bulkhead slot for the full
        // timeout duration, deliberately more than MaxConcurrency below so some of them genuinely
        // queue for a slot rather than all starting at once.
        var hangingEndpointIds = new List<WebhookEndpointId>();
        for (var i = 0; i < 5; i++)
        {
            var hangingEndpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
                seedDb, hangingSite, new Uri(hangingCrm.BaseAddress, "webhooks/deliver"), Now);
            hangingEndpointIds.Add(hangingEndpoint.Id);
        }

        var healthyEndpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            seedDb, healthySite, new Uri(succeedingCrm.BaseAddress, "webhooks/deliver"), Now);

        var (services, _) = WebhookDispatchTestHarness.BuildConsumerServices(
            fixture,
            WebhookDispatchTestHarness.ResilienceOptions(
                timeout: TimeSpan.FromSeconds(1), maxRetryAttempts: 0, maxConcurrency: 2, maxQueuedActions: 16),
            WebhookDispatchTestHarness.HttpOptions(responseHeadersTimeout: TimeSpan.FromSeconds(1)));
        await using var servicesDisposable = services;

        async Task<TimeSpan> TimedDispatchAsync(SiteId siteId, Guid conversationId)
        {
            using var scope = services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<DispatchWebhooksForEventHandler>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await handler.HandleAsync(
                new DispatchWebhooksForConversationEvent(Guid.NewGuid(), siteId, conversationId, WebhookEventTypes.ConversationClosed, Now),
                CancellationToken.None);
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }

        var hangingTask = TimedDispatchAsync(hangingSite, Guid.NewGuid());
        var healthyTask = TimedDispatchAsync(healthySite, Guid.NewGuid());
        await Task.WhenAll(hangingTask, healthyTask);

        var hangingElapsed = await hangingTask;
        var healthyElapsed = await healthyTask;

        // The real proof: the hanging tenant's own bulkhead (5 endpoints, MaxConcurrency 2) forces
        // at least two full ~1s timeout "waves," so its own dispatch genuinely takes multiple
        // seconds - and the healthy tenant, running at the very same time, is nowhere close to that,
        // even though both went through the same WebhookResiliencePipelines instance.
        Assert.True(hangingElapsed >= TimeSpan.FromSeconds(1.8),
            $"Expected the saturated tenant's own dispatch to take at least two ~1s bulkhead waves; took {hangingElapsed}.");
        Assert.True(healthyElapsed < TimeSpan.FromMilliseconds(500),
            $"Expected the healthy tenant's dispatch to stay fast despite the other tenant being saturated at the same time; took {healthyElapsed}.");

        await using var verify = fixture.CreateDbContext();
        var healthyDelivery = verify.WebhookDeliveries.Single(d => d.EndpointId == healthyEndpoint.Id);
        Assert.Equal(WebhookDeliveryStatus.Delivered, healthyDelivery.Status);

        // Scoped to exactly this test's own 5 endpoint ids, not "everything but the healthy one" - the
        // shared collection fixture's Postgres container outlives this one test, so a broader filter
        // would also pick up rows other tests in the same collection wrote (found by running this test
        // after WebhookDispatchBreakerTests in the same run and getting 9 rows instead of 5).
        var hangingDeliveries = verify.WebhookDeliveries.Where(d => hangingEndpointIds.Contains(d.EndpointId)).ToList();
        Assert.Equal(5, hangingDeliveries.Count);
        Assert.All(hangingDeliveries, d => Assert.Equal(WebhookDeliveryStatus.DeadLettered, d.Status));
    }
}
