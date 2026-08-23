using System.Net;
using System.Net.Sockets;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Module;
using Ago.Chat.Webhooks;
using Ago.Platform.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;

var builder = WebApplication.CreateBuilder(args);

new ChatModule().ConfigureServices(builder.Services, builder.Configuration);

// `6-05`: this dispatcher's own layered-timeout/retry/breaker/bulkhead options -
// WebhookResiliencePipelines' own remarks explain the split between what
// Ago.Platform.Resilience.ResiliencePipelineOptions binds (Timeout/CircuitBreaker/Bulkhead, and this
// class's own Retry *numbers*, even though the retry loop itself is hand-rolled) and what
// WebhookHttpOptions binds (connect/response-headers timeouts, an HTTP-transport-specific concern the
// shared options shape was never meant to carry).
builder.Services.AddResiliencePipelineOptions(WebhookResiliencePipelines.PipelineName, builder.Configuration, ConfigureResilienceDefaults);
// Unwrapped to a plain value once, here - the same "IOptions<T>.Value handed to the singleton, never
// IOptionsMonitor<T> passed down" shape ChatModule already uses for MessageSendRateLimitOptions/
// AttachmentOptions, adapted for a *named* options group via IOptionsMonitor<T>.Get(name) instead of
// IOptions<T>.Value. WebhookResiliencePipelines/HttpWebhookDeliveryClient's own remarks explain why:
// every endpoint/site shares the same configured thresholds, so there is nothing for either of them to
// gain from holding the live monitor instead of the value it would resolve to on every call anyway.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(WebhookResiliencePipelines.PipelineName));
builder.Services.AddSingleton<WebhookResiliencePipelines>();

builder.Services
    .AddOptions<WebhookHttpOptions>()
    .Bind(builder.Configuration.GetSection(WebhookHttpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// SocketsHttpHandler.ConnectTimeout is set once, here, at handler-construction time - the connect
// layer of resilience.md's "connect, response-headers, total" (WebhookHttpOptions' own remarks). A
// second, separate handler per named HttpClient (not shared with anything else this process might one
// day need) - this dispatcher's own outbound calls should never be affected by, or affect, any other
// HTTP client's connection pool or timeout configuration.
//
// ConnectCallback is the delivery-time SSRF recheck adr/0024 flags as this dispatcher's own obligation
// (WebhookUrlValidator's own remarks): resolves DNS itself, rejects every candidate address that is
// private/loopback/link-local, and connects to the one validated address directly rather than handing
// SocketsHttpHandler a bare hostname to resolve (and potentially re-resolve differently) a second time
// internally - the whole point is closing the gap between "what this checked" and "what it actually
// connects to."
builder.Services.AddHttpClient<IWebhookDeliveryClient, HttpWebhookDeliveryClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var connectTimeout = sp.GetRequiredService<IOptions<WebhookHttpOptions>>().Value.ConnectTimeout;
        return new SocketsHttpHandler
        {
            ConnectTimeout = connectTimeout,
            ConnectCallback = ConnectWithSsrfRecheckAsync,
        };
    });

builder.Services.AddScoped<DispatchWebhooksForEventHandler>();

builder.Services
    .AddOptions<ConversationAssignmentWebhookDispatchConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConversationAssignmentWebhookDispatchConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationAssignmentWebhookDispatchConsumer>();

builder.Services
    .AddOptions<ConversationClosedWebhookDispatchConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConversationClosedWebhookDispatchConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationClosedWebhookDispatchConsumer>();

// Liveness stays trivial; readiness means "can actually reach Postgres (the webhook_deliveries write
// side) and RabbitMQ (both consumers' own subscriptions)" - the same shape Ago.Chat.Worker's own
// Program.cs already established (2-04) for the same reason: this host has real dependencies to
// report on now, unlike the `dotnet new worker` skeleton it replaces.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

// Fixed historical values - an unmeasured starting point (CLAUDE.md: "do not invent numbers... measure
// or stay silent"), the same category as Ago.Platform.Caching.Redis/Storage.S3's own
// ConfigureDefaults. Bulkhead is the one group neither of those two wires up (resilience.md's Redis/S3
// rows do not call for one) - this is its first real caller (`6-01`'s own remarks).
static void ConfigureResilienceDefaults(ResiliencePipelineOptions options)
{
    options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(3) };
    options.Retry = new ResilienceRetryOptions
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        Delay = TimeSpan.FromMilliseconds(200),
    };
    options.CircuitBreaker = new ResilienceCircuitBreakerOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 2,
        SamplingDuration = TimeSpan.FromSeconds(10),
        BreakDuration = TimeSpan.FromSeconds(5),
    };
    options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 4, MaxQueuedActions = 16 };
}

// `6-05`: the delivery-time SSRF recheck adr/0024 flags as this dispatcher's own obligation - resolves
// the target host itself (SocketsHttpHandler would otherwise resolve and connect in one opaque step),
// rejects every candidate address WebhookUrlValidator.IsDisallowedResolvedAddress would also reject at
// registration time, and connects directly to the one validated address - never handing the hostname
// back to the OS resolver a second time inside the connect itself, which would reopen the exact TOCTOU
// window this exists to close (a second resolution could legitimately return a different address than
// the one just validated).
static async ValueTask<Stream> ConnectWithSsrfRecheckAsync(
    SocketsHttpConnectionContext context, CancellationToken cancellationToken)
{
    var candidates = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
    var address = Array.Find(candidates, a => !WebhookUrlValidator.IsDisallowedResolvedAddress(a))
        ?? throw new WebhookSsrfBlockedException(context.DnsEndPoint.Host);

    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }
    catch
    {
        socket.Dispose();
        throw;
    }
}
