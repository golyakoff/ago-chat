using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Module;
using Ago.Chat.Webhooks;
using Ago.Platform.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;

// `6-06`: this Program.cs is `src/Ago.Chat.Webhooks/Program.cs` with exactly one intentional
// removal, called out at the point it happens below - see this project's own .csproj header comment
// and load/reports/2026-08-23-webhooks-load-proof.md for the full reasoning. Every other line -
// ChatModule registration, resilience-options binding, the real hosted consumers - is the same
// composition the production host uses, so the breaker/bulkhead/timeout behaviour this proves is the
// real product code's, not a reimplementation of it.

// `--seed <https-or-http-url>`: registers one active WebhookEndpoint for the load-test site directly
// via EF Core (bypassing RegisterWebhookEndpointHandler's own HTTP API, the same "tests write
// webhook_deliveries/webhook_endpoints rows directly" precedent WebhookDispatchTestHarness.
// RegisterEndpointAsync already establishes) and exits. Bypassing that API here is deliberate, not a
// shortcut around it: WebhookUrlValidator's registration-time https-only/SSRF check is a real, already
// -proven (6-03's own tests) product behaviour this load test has no need to re-exercise, and a fake
// CRM this session can only run on http://localhost could never pass it regardless. Uses the same
// SiteId as deploy/seed/create-demo-tenant.sh's demo site so the load driver's visitor/operator
// traffic and this endpoint's dispatch events share one tenant, matching how a real shop with one
// registered CRM endpoint actually looks.
if (args is ["--seed", var rawUrl])
{
    var connectionString = Environment.GetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set AGO_CHAT_CONNECTION_STRING first.");
    var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connectionString).Options;
    await using var db = new AgoChatDbContext(options);

    var siteId = new SiteId(Guid.Parse("00000000-0000-0000-0000-000000000001")); // demo site, local-dev.md
    var cipher = new WebhookSecretCipher(new WebhookSecretCipherOptions
    {
        // Same fixed, published, test-only key src/Ago.Chat.Webhooks/appsettings.Development.json
        // already ships (Webhooks:SecretEncryptionKey) - reused here, not invented, so this runner's
        // own appsettings (a straight copy of that file) decrypts what this seeds without drift.
        SecretEncryptionKey = "Vg1G2KjonUB1uH8trETJzr30EPoeqt0YRGzYibDKy1o=",
    });
    // Must match Ago.Chat.FakeCrm's own configured FakeCrm:SigningSecret (its own
    // appsettings.Development.json - "fake-crm-test-signing-secret-do-not-use-in-prod") - the
    // dispatcher signs with *this endpoint's* registered secret and the fake CRM verifies the
    // signature against its own configured one; a mismatch here is a 401 from FakeCrm's own signature
    // check, not the hang/timeout this load test means to exercise (found live: the first seed used a
    // different made-up secret and every delivery dead-lettered on 401 in under a second).
    var endpoint = WebhookEndpoint.Register(
        new WebhookEndpointId(Guid.NewGuid()), siteId, new Uri(rawUrl),
        cipher.Encrypt("fake-crm-test-signing-secret-do-not-use-in-prod"), DateTimeOffset.UtcNow);
    db.WebhookEndpoints.Add(endpoint);
    await db.SaveChangesAsync();
    Console.WriteLine($"Seeded WebhookEndpoint {endpoint.Id.Value} -> {rawUrl} for site {siteId.Value}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

new ChatModule().ConfigureServices(builder.Services, builder.Configuration);

builder.Services.AddResiliencePipelineOptions(WebhookResiliencePipelines.PipelineName, builder.Configuration, ConfigureResilienceDefaults);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(WebhookResiliencePipelines.PipelineName));
builder.Services.AddSingleton<WebhookResiliencePipelines>();

builder.Services
    .AddOptions<WebhookHttpOptions>()
    .Bind(builder.Configuration.GetSection(WebhookHttpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// `6-06`'s one deliberate difference from src/Ago.Chat.Webhooks/Program.cs: no ConnectCallback here.
// The production host's ConnectWithSsrfRecheckAsync resolves the target host's DNS itself and rejects
// every private/loopback/link-local candidate address immediately before connecting (adr/0024's TOCTOU
// close) - correct and load-bearing in production, and *why* this load test cannot point the real host
// at a fake CRM this machine can only run on a private address. Everything downstream of this handler
// (timeout, retry, breaker, bulkhead, signing, delivery recording) is unchanged from production.
builder.Services.AddHttpClient<IWebhookDeliveryClient, HttpWebhookDeliveryClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var connectTimeout = sp.GetRequiredService<IOptions<WebhookHttpOptions>>().Value.ConnectTimeout;
        return new System.Net.Http.SocketsHttpHandler { ConnectTimeout = connectTimeout };
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

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

// Identical to src/Ago.Chat.Webhooks/Program.cs's own ConfigureResilienceDefaults - the real
// production starting point (CLAUDE.md: "do not invent numbers"), overridable by this project's own
// appsettings.Development.json's Resilience:Webhooks section exactly like the real host.
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
