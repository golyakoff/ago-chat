using System.Collections.Concurrent;
using Ago.Chat.Domain;
using Ago.Platform.Resilience;
using Polly;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`: the stateful half of `resilience.md`'s "Inside the dispatcher" list - a circuit breaker's
/// open/half-open state and a bulkhead's in-flight count only mean anything if the *same*
/// <see cref="ResiliencePipeline"/> instance is reused across calls for the same key, never rebuilt
/// per call. Two independent caches, matching the backlog's own two independent keys:
/// <list type="bullet">
/// <item><see cref="GetEndpointPipeline"/> - Timeout + CircuitBreaker, keyed by
/// <see cref="WebhookEndpointId"/>, so one tenant's dead CRM opens only *that* endpoint's breaker
/// (backlog: "Breaker proven per-endpoint, not global").</item>
/// <item><see cref="GetSiteBulkheadPipeline"/> - Bulkhead only, keyed by <see cref="SiteId"/>, so a
/// tenant with many endpoints (or one slow one) cannot starve delivery capacity belonging to any
/// other tenant (backlog: "Bulkhead proven per-tenant").</item>
/// </list>
/// Every key shares the *same configured thresholds* (one `Resilience:Webhooks:*` section) - only the
/// pipeline *instance*, and therefore its state, differs per key. Registered as a singleton
/// (`Program.cs`): a scoped or transient lifetime would silently rebuild a fresh, un-tripped breaker
/// on every DI scope, defeating the entire point of "consecutive failures open the breaker."
///
/// Retry is deliberately absent from both pipelines here - see <see cref="HttpWebhookDeliveryClient"/>'s
/// own remarks for why this dispatcher hand-rolls its jittered backoff loop around
/// <see cref="GetEndpointPipeline"/> instead of composing `Ago.Platform.Resilience.ResiliencePolicyBuilder.WithRetry`
/// into it.
///
/// Takes the already-bound <see cref="ResiliencePipelineOptions"/> value directly, not
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> - every named-options group in
/// this codebase eventually gets unwrapped to a plain unwrapped value for its one real consumer
/// (`ChatModule`'s own `MessageSendRateLimitOptions`/`AttachmentOptions`, `sp.GetRequiredService
/// &lt;IOptions&lt;T&gt;&gt;().Value`); `Program.cs` does the same here via
/// `IOptionsMonitor&lt;ResiliencePipelineOptions&gt;.Get(PipelineName)`, once, at registration time.
/// The benefit compounds for a type built specifically to be constructed directly in a test
/// (`Ago.Chat.Integration.Tests`, no ASP.NET Core host, no options-binding pipeline to stand up) - a
/// plain object literal is trivial to build there; a working fake
/// <c>IOptionsMonitor&lt;ResiliencePipelineOptions&gt;</c> is not.
/// </summary>
public sealed class WebhookResiliencePipelines(ResiliencePipelineOptions options)
{
    public const string PipelineName = "Webhooks";

    private readonly ConcurrentDictionary<WebhookEndpointId, ResiliencePipeline> _endpointPipelines = new();
    private readonly ConcurrentDictionary<SiteId, ResiliencePipeline> _siteBulkheadPipelines = new();

    /// <summary>CircuitBreaker wrapping Timeout, deliberately in that order (breaker outermost) -
    /// composing them the other way round (the order `Ago.Platform.Storage.S3.ServiceCollectionExtensions`
    /// itself uses, `.WithRetry().WithTimeout().WithCircuitBreaker()`) silently made the breaker never
    /// open for a genuinely hanging endpoint: Polly's own timeout strategy cancels its *internal*
    /// linked token and only converts that cancellation into <c>TimeoutRejectedException</c> at its own
    /// layer, on the way back out - if CircuitBreaker sits *inside* Timeout, the raw
    /// <see cref="OperationCanceledException"/> reaches the breaker's `ShouldHandle` first (correctly
    /// excluded below, the same "cancellation is never the endpoint's fault" reasoning
    /// `RedisCache`/`S3FileStorage` already apply) and the later `TimeoutRejectedException` conversion
    /// happens one layer further out, past the point the breaker ever gets to see it - so a hanging
    /// endpoint's timeouts could never count as breaker failures, ever, regardless of `MinimumThroughput`.
    /// Found by running `WebhookDispatchBreakerTests` against a real hanging `Ago.Chat.FakeCrm` process
    /// and watching every one of six consecutive rounds pay the full timeout with the breaker never
    /// opening, not assumed from reading Polly's source. With CircuitBreaker outermost, Timeout's own
    /// conversion to `TimeoutRejectedException` happens *before* the exception ever reaches the
    /// breaker, so `IsBreakerWorthy` sees the type it actually checks for.
    ///
    /// Excludes <see cref="OperationCanceledException"/> (the same convention `RedisCache`/
    /// `S3FileStorage` already use - genuine caller cancellation, e.g. consumer shutdown, is never "the
    /// endpoint's fault") and, like `S3FileStorage`'s own `IsTransient`, excludes a client-error HTTP
    /// response (`WebhookNonSuccessResponseException` with a 4xx status) too - a tenant's receiver
    /// rejecting *this* payload's content is not evidence the endpoint itself is unreachable, the same
    /// "an expected outcome should not trip a breaker built for unexpected ones" reasoning
    /// `S3FileStorage`'s own comment gives for excluding a 404.</summary>
    public ResiliencePipeline GetEndpointPipeline(WebhookEndpointId endpointId) =>
        _endpointPipelines.GetOrAdd(endpointId, _ => new ResiliencePolicyBuilder(PipelineName)
            .WithCircuitBreaker(options.CircuitBreaker!, IsBreakerWorthy)
            .WithTimeout(options.Timeout!)
            .Build());

    /// <summary>Bulkhead only, wrapping one endpoint's *entire* delivery-with-retries operation - a
    /// slot is held for the full duration of every attempt and every backoff wait in between, which is
    /// the point: a hanging endpoint should occupy exactly one of its tenant's limited concurrent
    /// slots for as long as this dispatcher is still working it, not release the slot between
    /// attempts only to reacquire it a moment later.</summary>
    public ResiliencePipeline GetSiteBulkheadPipeline(SiteId siteId) =>
        _siteBulkheadPipelines.GetOrAdd(siteId, _ => new ResiliencePolicyBuilder(PipelineName).WithBulkhead(options.Bulkhead!).Build());

    private static bool IsBreakerWorthy(Exception ex) =>
        ex is not OperationCanceledException
        && ex is not WebhookNonSuccessResponseException { StatusCode: >= 400 and < 500 }
        // A blocked-by-SSRF-policy address (WebhookSsrfBlockedException's own remarks) says nothing
        // about whether the real endpoint is reachable - it would block identically on every future
        // attempt regardless of the endpoint's own health, so counting it toward "this endpoint is
        // failing" would be attributing a policy decision to the wrong cause.
        && ex is not WebhookSsrfBlockedException;
}
