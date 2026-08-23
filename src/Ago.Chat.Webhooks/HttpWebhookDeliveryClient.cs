using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Ago.Platform.Resilience;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-05`'s <see cref="IWebhookDeliveryClient"/> implementation - lives in this host, not a shared
/// Infrastructure project, the same "genuinely single-host adapter behind an Application port" shape
/// `Ago.Chat.Worker.SkipLockedAssignmentClaimer`/`RedisLockAssignmentClaimer` already establish for
/// `IAssignmentClaimer`: nothing outside `Ago.Chat.Webhooks` ever calls a tenant's CRM, so there is no
/// second host to share this class with.
///
/// Composes three of `resilience.md`'s four dispatcher patterns straight from
/// <see cref="WebhookResiliencePipelines"/> (Timeout, CircuitBreaker, Bulkhead) - the fourth,
/// **bounded retry with exponential backoff and jitter**, is hand-rolled in
/// <see cref="DeliverWithRetryAsync"/> rather than composed from
/// `Ago.Platform.Resilience.ResiliencePolicyBuilder.WithRetry`: that builder's
/// <c>Polly.Retry.RetryStrategyOptions</c> construction (`ResiliencePolicyBuilder.cs`, `6-01`) never
/// sets <c>UseJitter</c> or a <c>DelayGenerator</c> and exposes no way for a caller to, so it cannot
/// express "and jitter" - a requirement this specific boundary states explicitly
/// (`resilience.md`/the backlog) where neither `Ago.Platform.Caching.Redis` nor
/// `Ago.Platform.Storage.S3` needed retry-with-jitter at all. Rather than widen the shared builder's
/// public surface for this one caller (a change to `ago-platform`, out of this item's own scope, and
/// a bigger commitment than one boundary's own need justifies), this hand-rolls the jittered-backoff
/// loop around <see cref="WebhookResiliencePipelines.GetEndpointPipeline"/> (still reused for Timeout
/// + CircuitBreaker) while reading its retry *numbers* from the same
/// <c>Resilience:Webhooks:Retry:*</c> config section every other named pipeline's own group already
/// binds through (`ResiliencePipelineOptions.Retry`) - only the execution mechanism, not the
/// configuration shape, diverges from the shared pattern.
///
/// The retry-worthiness predicate, stated explicitly per the backlog's own ask: a transient
/// server-side signal (5xx, a timeout at any layer) gets the full configured retry budget with
/// backoff - the endpoint is there and might recover. A refused or reset connection
/// (<see cref="FailureClassification.ConnectionGone"/>, `6-04`'s <c>disappears</c> personality) gets
/// none - the far end is provably not listening *right now*, and hammering the same dead socket a few
/// hundred milliseconds apart, inside the same delivery pass, wastes the bulkhead slot and the
/// breaker's own failure budget without any real chance the next attempt behaves differently; the
/// honest next opportunity is a future event or the tenant fixing their endpoint, not a tighter retry
/// loop here. A 4xx (the endpoint responding, just rejecting this content) also gets none - a
/// permanent rejection, not a transient one - and, like `S3FileStorage.IsTransient`'s own exclusion of
/// a 404, is excluded from circuit-breaker consideration too (`WebhookResiliencePipelines.IsBreakerWorthy`).
/// An already-open breaker (<see cref="BrokenCircuitException"/>) also gets none - `resilience.md`:
/// "parked, not retried in a hot loop."
/// </summary>
public sealed class HttpWebhookDeliveryClient(
    HttpClient httpClient,
    WebhookResiliencePipelines pipelines,
    ResiliencePipelineOptions resilienceOptions,
    IOptions<WebhookHttpOptions> httpOptions,
    IClock clock,
    ILogger<HttpWebhookDeliveryClient> logger) : IWebhookDeliveryClient
{
    private enum FailureClassification
    {
        TransientServerOrTimeout,
        ClientRejected,
        ConnectionGone,
        BreakerOpen,
        Unexpected,
    }

    public async Task<WebhookDeliveryAttemptOutcome> DeliverAsync(
        WebhookEndpoint endpoint, string eventType, string payloadJson, string signingSecret, CancellationToken cancellationToken)
    {
        var bulkhead = pipelines.GetSiteBulkheadPipeline(endpoint.SiteId);
        try
        {
            return await bulkhead.ExecuteAsync(
                async ct => await DeliverWithRetryAsync(endpoint, payloadJson, signingSecret, ct), cancellationToken);
        }
        catch (RateLimiterRejectedException)
        {
            // The per-tenant bulkhead is full for this site (MaxConcurrency in-flight, MaxQueuedActions
            // already waiting too) - no HTTP attempt was ever made, so this is recorded exactly like any
            // other dead-lettered outcome rather than surfaced as an unhandled exception that would
            // needlessly poison-message the whole event.
            logger.LogWarning(
                "Webhook delivery to endpoint {EndpointId} (site {SiteId}) rejected by the per-tenant bulkhead.",
                endpoint.Id.Value, endpoint.SiteId.Value);
            return new WebhookDeliveryAttemptOutcome(0, WebhookDeliveryStatus.DeadLettered, null, "Rejected by per-tenant concurrency limit.");
        }
    }

    private async Task<WebhookDeliveryAttemptOutcome> DeliverWithRetryAsync(
        WebhookEndpoint endpoint, string payloadJson, string signingSecret, CancellationToken cancellationToken)
    {
        var endpointPipeline = pipelines.GetEndpointPipeline(endpoint.Id);
        var retry = resilienceOptions.Retry!;

        var attemptNumber = 0;
        while (true)
        {
            attemptNumber++;
            try
            {
                var (statusCode, snippet) = await endpointPipeline.ExecuteAsync(
                    async ct => await SendOnceAsync(endpoint.Url, payloadJson, signingSecret, ct), cancellationToken);
                return new WebhookDeliveryAttemptOutcome(attemptNumber, WebhookDeliveryStatus.Delivered, statusCode, snippet);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var classification = Classify(ex);
                if (!ShouldRetry(classification, attemptNumber, retry.MaxRetryAttempts))
                {
                    var (statusCode, snippet) = ExtractResponseInfo(ex);
                    logger.LogWarning(
                        ex,
                        "Webhook delivery to endpoint {EndpointId} dead-lettered after {Attempts} attempt(s) ({Classification}).",
                        endpoint.Id.Value, attemptNumber, classification);
                    return new WebhookDeliveryAttemptOutcome(
                        attemptNumber, WebhookDeliveryStatus.DeadLettered, statusCode, snippet ?? Truncate(ex.Message));
                }

                await Task.Delay(ComputeBackoffWithJitter(retry.Delay, attemptNumber), cancellationToken);
            }
        }
    }

    private async Task<(int StatusCode, string? Snippet)> SendOnceAsync(
        Uri url, string payloadJson, string signingSecret, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signatureHeader = WebhookSignature.BuildHeader(bodyBytes, clock.UtcNow.ToUnixTimeSeconds(), signingSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bodyBytes) };
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Ago-Signature", signatureHeader);

        // The middle layer of "connect, response-headers, total" (resilience.md) - connect is the
        // HttpClient's own SocketsHttpHandler.ConnectTimeout (Program.cs, set once at construction);
        // total is WithTimeout above, wrapping this whole method including this one. This CTS is
        // linked to `cancellationToken` (the ambient token WithTimeout/the caller supplied) so either
        // deadline - ours or the outer one - ends the wait, whichever comes first.
        using var headersTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeoutCts.CancelAfter(httpOptions.Value.ResponseHeadersTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // headersTimeoutCts fired on its own - the ambient token (the caller's, or Polly's own
            // total-timeout token further up the pipeline) was not the one that cancelled this call,
            // so this is genuinely our own response-headers deadline, not a caller giving up or the
            // total timeout already handling it. If the ambient token *was* the one that fired,
            // `cancellationToken.IsCancellationRequested` is true, this filter does not match, and the
            // OperationCanceledException propagates untouched - the total-timeout Polly strategy
            // wrapping this call (WebhookResiliencePipelines.GetEndpointPipeline) is what converts
            // that into a TimeoutRejectedException, not this method.
            throw new WebhookResponseHeadersTimeoutException();
        }

        using (response)
        {
            var snippet = await ReadBoundedSnippetAsync(response.Content, cancellationToken);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                return ((int)response.StatusCode, snippet);
            }

            throw new WebhookNonSuccessResponseException((int)response.StatusCode, snippet);
        }
    }

    private static async Task<string?> ReadBoundedSnippetAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // Bounded read, not "read it all then truncate" - a hostile or merely buggy endpoint streaming
        // an unbounded body must never make this dispatcher buffer it all into memory first
        // (`WebhookDelivery.MaxResponseSnippetLength` is the same cap the domain aggregate itself
        // enforces on write, reused here so the read side never does pointless extra work capturing
        // bytes the write side would only throw away).
        const int maxBytes = WebhookDelivery.MaxResponseSnippetLength;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maxBytes];
        var totalRead = 0;
        int read;
        while (totalRead < maxBytes
            && (read = await stream.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), cancellationToken)) > 0)
        {
            totalRead += read;
        }

        return totalRead == 0 ? null : Encoding.UTF8.GetString(buffer, 0, totalRead);
    }

    private static FailureClassification Classify(Exception ex) => ex switch
    {
        BrokenCircuitException => FailureClassification.BreakerOpen,
        TimeoutRejectedException => FailureClassification.TransientServerOrTimeout,
        WebhookResponseHeadersTimeoutException => FailureClassification.TransientServerOrTimeout,
        WebhookNonSuccessResponseException { StatusCode: >= 500 } => FailureClassification.TransientServerOrTimeout,
        WebhookNonSuccessResponseException => FailureClassification.ClientRejected,
        // Checked before the generic HttpRequestException cases below: SocketsHttpHandler.ConnectCallback
        // exceptions (Program.cs's own SSRF recheck) surface wrapped in HttpRequestException, same as a
        // genuine connection failure - without this ahead of the catch-all, a blocked address would fall
        // through to "HttpRequestException => TransientServerOrTimeout" and get retried repeatedly
        // against an address that will never stop being blocked (WebhookSsrfBlockedException's own
        // remarks for why that would be wrong).
        WebhookSsrfBlockedException or HttpRequestException { InnerException: WebhookSsrfBlockedException } =>
            FailureClassification.Unexpected,
        HttpRequestException when FindSocketException(ex) is { SocketErrorCode: var code } && IsConnectionGone(code) =>
            FailureClassification.ConnectionGone,
        HttpRequestException => FailureClassification.TransientServerOrTimeout,
        _ => FailureClassification.Unexpected,
    };

    // .NET's HttpClient does not always surface a "disappears"-style TCP-level failure as
    // HttpRequestException.InnerException being a SocketException directly - a reset mid-response can
    // arrive as HttpRequestException -> IOException -> SocketException, one level deeper. Found by
    // actually running this dispatcher against 6-04's real DisappearedConnectionListener and watching
    // it retry 3 times instead of failing after 1 - a single-level InnerException check silently fell
    // through to the generic "HttpRequestException => TransientServerOrTimeout" branch below it and
    // treated a refused/reset connection exactly like a transient 5xx. Walking the full chain (bounded,
    // never infinite - InnerException forms a DAG-free chain in practice) is what actually finds it
    // regardless of how many wrapper exceptions .NET's networking stack interposed.
    private static SocketException? FindSocketException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SocketException socketException)
            {
                return socketException;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    private static bool IsConnectionGone(SocketError error) => error is
        SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted
        or SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable;

    private static bool ShouldRetry(FailureClassification classification, int attemptNumber, int maxRetryAttempts) =>
        classification == FailureClassification.TransientServerOrTimeout && attemptNumber <= maxRetryAttempts;

    private static (int? StatusCode, string? Snippet) ExtractResponseInfo(Exception ex) => ex switch
    {
        WebhookNonSuccessResponseException e => (e.StatusCode, e.ResponseSnippet),
        _ => (null, null),
    };

    // AWS's "full jitter" (https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/):
    // sleep = uniform(0, min(cap, base * 2^attempt)) - chosen over a fixed +/-percentage jitter
    // (RedisCache.Jitter's own shape, used there for cache-TTL desynchronisation, a different problem)
    // because full jitter is the specific formula that also caps the *worst-case* contention when many
    // endpoints retry at once, which a narrow +/-10% band does not: two callers whose un-jittered delays
    // would have collided can still land arbitrarily far apart under full jitter, not just slightly
    // offset. Capped at 30s so a small MaxRetryAttempts config change can never accidentally produce a
    // multi-minute wait between attempts inside one delivery pass.
    private static TimeSpan ComputeBackoffWithJitter(TimeSpan baseDelay, int attemptNumber)
    {
        var exponentialMs = baseDelay.TotalMilliseconds * Math.Pow(2, attemptNumber - 1);
        var cappedMs = Math.Min(exponentialMs, TimeSpan.FromSeconds(30).TotalMilliseconds);
        var jitteredMs = Random.Shared.NextDouble() * cappedMs;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    private static string Truncate(string value) =>
        value.Length > WebhookDelivery.MaxResponseSnippetLength ? value[..WebhookDelivery.MaxResponseSnippetLength] : value;
}
