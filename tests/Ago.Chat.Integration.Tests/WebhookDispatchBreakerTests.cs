using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;
using Ago.Chat.FakeCrm.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s per-endpoint breaker Done-when: two endpoints for the same site, one pointed at a real
/// `Ago.Chat.FakeCrm` process configured to hang (so every pre-breaker attempt genuinely spends its
/// full timeout budget), one pointed at a real `succeeds` process - the failing endpoint's breaker
/// opens and stops attempting (proven by wall-clock time collapsing from "one full timeout" to "near
/// instant" once open, not merely asserted from the exception type), while the succeeding endpoint
/// keeps delivering throughout, including while several events race concurrently.
///
/// Uses <see cref="WebhookDispatchTestHarness.BuildConsumerServices"/> (a fresh, properly scoped
/// <c>AgoChatDbContext</c> per call via <c>IServiceScopeFactory</c>), not the simpler
/// <see cref="WebhookDispatchTestHarness.CreateHandler"/> - that one binds a handler to a single shared
/// `AgoChatDbContext`, which is not thread-safe: the "5 concurrent rounds" section below genuinely
/// needs several `DispatchWebhooksForEventHandler.HandleAsync` calls in flight at once, and EF Core
/// throws `InvalidOperationException` ("a second operation was started on this context instance")
/// the moment two of them touch one shared context concurrently - found by running this test with
/// `CreateHandler` first and hitting exactly that.
/// </summary>
[Collection(WebhookDispatchCollection.Name)]
public sealed class WebhookDispatchBreakerTests(WebhookDispatchFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneEndpointsBreakerOpens_WhileTheOtherKeepsDeliveringConcurrently()
    {
        await using var hangingCrm = new FakeCrmProcessFixture { DefaultBehavior = "hang" };
        await using var succeedingCrm = new FakeCrmProcessFixture { DefaultBehavior = "succeeds" };
        await Task.WhenAll(hangingCrm.InitializeAsync(), succeedingCrm.InitializeAsync());

        await using var seedDb = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(seedDb);
        var hangingEndpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            seedDb, siteId, new Uri(hangingCrm.BaseAddress, "webhooks/deliver"), Now);
        var succeedingEndpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            seedDb, siteId, new Uri(succeedingCrm.BaseAddress, "webhooks/deliver"), Now);

        // MinimumThroughput 2, FailureRatio 0.5: the hanging endpoint's own 2nd consecutive timeout
        // opens its breaker. No retry (maxRetryAttempts: 0) - one timeout is enough to count as a
        // failure per round, keeping the pre-breaker warm-up itself fast to run.
        var (services, _) = WebhookDispatchTestHarness.BuildConsumerServices(
            fixture,
            WebhookDispatchTestHarness.ResilienceOptions(
                timeout: TimeSpan.FromMilliseconds(300), maxRetryAttempts: 0,
                minimumThroughput: 2, failureRatio: 0.5,
                samplingDuration: TimeSpan.FromSeconds(30), breakDuration: TimeSpan.FromSeconds(30)),
            WebhookDispatchTestHarness.HttpOptions(
                connectTimeout: TimeSpan.FromSeconds(1), responseHeadersTimeout: TimeSpan.FromMilliseconds(300)));
        await using var servicesDisposable = services;

        async Task<TimeSpan> DispatchOneRoundAsync(SiteId site)
        {
            using var scope = services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<DispatchWebhooksForEventHandler>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await handler.HandleAsync(
                new DispatchWebhooksForConversationEvent(Guid.NewGuid(), site, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, Now),
                CancellationToken.None);
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }

        // Warm-up: dispatch rounds one at a time until the breaker opens, proven the same way
        // throughout - wall-clock time collapsing from "paid the real ~300ms timeout" to "near
        // instant." Looped (bounded at 6 attempts) rather than hardcoding "exactly 2 failures opens
        // it": MinimumThroughput=2 is the configured floor, not a guarantee Polly's own sliding-window
        // statistics open the breaker at exactly the 2nd call - found by running this test with a
        // hardcoded 2-round warm-up and watching round 3 still pay the full timeout.
        var warmupRounds = new List<TimeSpan>();
        TimeSpan? firstFastRound = null;
        for (var i = 0; i < 6 && firstFastRound is null; i++)
        {
            var elapsed = await DispatchOneRoundAsync(siteId);
            warmupRounds.Add(elapsed);
            if (elapsed < TimeSpan.FromMilliseconds(150))
            {
                firstFastRound = elapsed;
            }
        }

        Assert.True(firstFastRound is not null,
            $"Expected the breaker to open (a round short-circuiting near-instantly) within 6 rounds; every round paid the full timeout: [{string.Join(", ", warmupRounds)}].");
        Assert.True(warmupRounds.Count >= 2,
            "Expected at least 2 slow rounds before the breaker opened - MinimumThroughput is 2, so it should never open on the very first call.");
        Assert.All(warmupRounds.Take(warmupRounds.Count - 1), elapsed => Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(250), $"Expected every pre-breaker round to pay the real ~300ms timeout; one took {elapsed}."));

        // Concurrency, not sequential luck: five more events for the same site, fired at once - each
        // with its own DbContext/scope, so this genuinely exercises concurrent handler invocations, not
        // just concurrent HTTP calls behind one handler call. If the hanging endpoint's open breaker
        // somehow still blocked or slowed the pipeline, five sequential ~300ms timeouts would take
        // ~1.5s; this must complete far faster, and the succeeding endpoint's own deliveries must all
        // still land.
        var concurrentStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DispatchOneRoundAsync(siteId)));
        concurrentStopwatch.Stop();
        Assert.True(concurrentStopwatch.Elapsed < TimeSpan.FromMilliseconds(800),
            $"Expected 5 concurrent rounds against an open breaker + a healthy endpoint to finish fast; took {concurrentStopwatch.Elapsed}.");

        await using var verify = fixture.CreateDbContext();
        var hangingDeliveries = verify.WebhookDeliveries.Where(d => d.EndpointId == hangingEndpoint.Id).ToList();
        var succeedingDeliveries = verify.WebhookDeliveries.Where(d => d.EndpointId == succeedingEndpoint.Id).ToList();

        // warmupRounds.Count + 5 concurrent rounds, for both endpoints (one attempted-and-dead-lettered
        // row per round, whether via a real timeout or a short-circuited breaker).
        var totalRounds = warmupRounds.Count + 5;
        Assert.Equal(totalRounds, hangingDeliveries.Count);
        Assert.All(hangingDeliveries, d => Assert.Equal(WebhookDeliveryStatus.DeadLettered, d.Status));

        Assert.Equal(totalRounds, succeedingDeliveries.Count);
        Assert.All(succeedingDeliveries, d => Assert.Equal(WebhookDeliveryStatus.Delivered, d.Status));
    }
}
