using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;
using Ago.Chat.FakeCrm.Tests;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`'s core Done-when, one real `Ago.Chat.FakeCrm` process per personality, driven through the
/// real <see cref="DispatchWebhooksForEventHandler"/> -&gt; <see cref="Ago.Chat.Webhooks.HttpWebhookDeliveryClient"/>
/// -&gt; real `HttpClient` path, no mocks anywhere in the chain. Each test owns its own process
/// (`FakeCrmProcessFixture.DefaultBehavior`, `6-05`'s own additive extension) rather than sharing one
/// via `FakeCrmCollection` - a fixed personality per process is exactly what proving four different
/// outcomes at once needs. Cleanup is `await using` alone, not a second explicit `DisposeAsync()` call
/// too - `FakeCrmProcessFixture.DisposeAsync` is not safe to call twice (a `Process` already disposed
/// once throws `InvalidOperationException` on a second `HasExited` check), found by actually running
/// these tests with a redundant `try/finally` around `await using` and watching every one of them fail
/// with that exact exception despite every assertion inside having already passed.
/// </summary>
[Collection(WebhookDispatchCollection.Name)]
public sealed class WebhookDispatchDeliveryOutcomeTests(WebhookDispatchFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Succeeds_DeliversAndIsRecordedDelivered()
    {
        await using var crm = new FakeCrmProcessFixture { DefaultBehavior = "succeeds" };
        await crm.InitializeAsync();

        await using var db = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(db);
        var endpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            db, siteId, new Uri(crm.BaseAddress, "webhooks/deliver"), Now);

        var (handler, _) = WebhookDispatchTestHarness.CreateHandler(
            db, WebhookDispatchTestHarness.ResilienceOptions(), WebhookDispatchTestHarness.HttpOptions());

        var result = await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(
                Guid.NewGuid(), siteId, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);

        await using var verify = fixture.CreateDbContext();
        var stored = verify.WebhookDeliveries.Single(d => d.EndpointId == endpoint.Id);
        Assert.Equal(WebhookDeliveryStatus.Delivered, stored.Status);
        Assert.Equal(1, stored.Attempt);
        Assert.Equal(200, stored.ResponseStatus);
        Assert.NotNull(stored.DeliveredAt);
    }

    [Fact]
    public async Task FiveXXs_RetriesTheConfiguredNumberOfTimesThenDeadLettersWithTheRealResponseCaptured()
    {
        await using var crm = new FakeCrmProcessFixture { DefaultBehavior = "5xx" };
        await crm.InitializeAsync();

        await using var db = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(db);
        var endpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            db, siteId, new Uri(crm.BaseAddress, "webhooks/deliver"), Now);

        // MaxRetryAttempts: 2 -> 3 total attempts (1 + 2 retries), matching ShouldRetry's own
        // "attemptNumber <= maxRetryAttempts" semantics (HttpWebhookDeliveryClient's own remarks).
        // minimumThroughput deliberately higher than the 3 attempts this test makes - this test is
        // about the *retry* budget in isolation, not the breaker; with the harness's own default
        // minimumThroughput (2), 2 consecutive 5xx failures would open the breaker before the 3rd
        // attempt ever reached the real FakeCrm process, and that 3rd attempt would dead-letter with a
        // BrokenCircuitException instead of the real captured 503 - found by running this test with
        // the default and seeing ResponseStatus come back null instead of 503, exactly that.
        var (handler, _) = WebhookDispatchTestHarness.CreateHandler(
            db,
            WebhookDispatchTestHarness.ResilienceOptions(
                maxRetryAttempts: 2, retryDelay: TimeSpan.FromMilliseconds(50), minimumThroughput: 10),
            WebhookDispatchTestHarness.HttpOptions());

        await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(
                Guid.NewGuid(), siteId, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, Now),
            CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var stored = verify.WebhookDeliveries.Single(d => d.EndpointId == endpoint.Id);
        Assert.Equal(WebhookDeliveryStatus.DeadLettered, stored.Status);
        Assert.Equal(3, stored.Attempt);
        Assert.Equal(503, stored.ResponseStatus); // the real status FakeCrm's own "5xx" personality answers
        Assert.Null(stored.DeliveredAt);
    }

    [Fact]
    public async Task Hangs_IsCutOffByTheTotalTimeoutRatherThanLeftToHangTheConsumerThread()
    {
        // The real "hangs 30s" personality (6-04's own scope), proven cut off well inside a
        // test-reasonable wall-clock budget by this dispatcher's own configured Timeout - not by the
        // test's own cancellation, and not by waiting anywhere near the full 30s.
        await using var crm = new FakeCrmProcessFixture { DefaultBehavior = "hang-30s" };
        await crm.InitializeAsync();

        await using var db = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(db);
        var endpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(
            db, siteId, new Uri(crm.BaseAddress, "webhooks/deliver"), Now);

        var (handler, _) = WebhookDispatchTestHarness.CreateHandler(
            db,
            WebhookDispatchTestHarness.ResilienceOptions(
                timeout: TimeSpan.FromSeconds(1), maxRetryAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(50)),
            WebhookDispatchTestHarness.HttpOptions(
                connectTimeout: TimeSpan.FromSeconds(1), responseHeadersTimeout: TimeSpan.FromSeconds(1)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var overallGuard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(
                Guid.NewGuid(), siteId, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, Now),
            overallGuard.Token);
        stopwatch.Stop();

        // Well under the 30s hang - this is the actual proof "not left to hang the consumer
        // thread," not an assertion derived from the config alone.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected the dispatch to be cut off well inside 10s; took {stopwatch.Elapsed}.");

        await using var verify = fixture.CreateDbContext();
        var stored = verify.WebhookDeliveries.Single(d => d.EndpointId == endpoint.Id);
        Assert.Equal(WebhookDeliveryStatus.DeadLettered, stored.Status);
        Assert.Null(stored.ResponseStatus); // never received a response at all
    }

    [Fact]
    public async Task Disappears_FailsFastAndDoesNotRetryAsAggressivelyAsATransient5xxWould()
    {
        // No X-Fake-Crm-Behavior header exists for "disappears" (6-04's own README: refusing a TCP
        // connection has to happen before any HTTP request is even readable) - it is selected by port,
        // not DefaultBehavior, so the main process's own personality is irrelevant here; only
        // crm.DisappearPort matters.
        await using var crm = new FakeCrmProcessFixture();
        await crm.InitializeAsync();

        await using var db = fixture.CreateDbContext();
        var siteId = await WebhookDispatchTestHarness.SeedSiteAsync(db);
        var disappearedUrl = new Uri($"http://127.0.0.1:{crm.DisappearPort}/webhooks/deliver");
        var endpoint = await WebhookDispatchTestHarness.RegisterEndpointAsync(db, siteId, disappearedUrl, Now);

        // A generous retry budget on purpose (5) - if the "connection gone" classification failed
        // to suppress retries, this test would still pass by accident with a tight budget. Proving
        // "does not retry as aggressively as a transient 5xx would" needs a budget the dispatcher
        // could have burned through if it treated this like any other transient failure.
        var (handler, _) = WebhookDispatchTestHarness.CreateHandler(
            db,
            WebhookDispatchTestHarness.ResilienceOptions(maxRetryAttempts: 5, retryDelay: TimeSpan.FromMilliseconds(200)),
            WebhookDispatchTestHarness.HttpOptions());

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var overallGuard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await handler.HandleAsync(
            new DispatchWebhooksForConversationEvent(
                Guid.NewGuid(), siteId, Guid.NewGuid(), WebhookEventTypes.ConversationClosed, Now),
            overallGuard.Token);
        stopwatch.Stop();

        await using var verify = fixture.CreateDbContext();
        var stored = verify.WebhookDeliveries.Single(d => d.EndpointId == endpoint.Id);
        Assert.Equal(WebhookDeliveryStatus.DeadLettered, stored.Status);
        // The real proof of "does not retry as aggressively": exactly one attempt, not up to the
        // 5-attempt budget a transient 5xx would have spent with backoff between each.
        Assert.Equal(1, stored.Attempt);
        // With zero retries and no backoff wait, this resolves near-instantly - nowhere close to
        // what 5 exponential-backed-off attempts (200ms, 400ms, 800ms, 1600ms, 3200ms - several
        // seconds) would have taken.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected a fast failure with no retry backoff; took {stopwatch.Elapsed}.");
    }
}
