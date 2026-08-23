using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.DispatchWebhooksForEvent;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Webhooks;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `6-05`: builds the real dispatch stack (`DispatchWebhooksForEventHandler` ->
/// `HttpWebhookDeliveryClient` -> a real `HttpClient` -> `Ago.Chat.FakeCrm`) by hand, the same "no
/// ASP.NET Core host, construct the pieces directly" shape
/// `ConversationAssignmentFanoutEndToEndTests.BuildServiceProvider` already uses - a test proving
/// breaker/bulkhead/retry behaviour needs to *tune* those thresholds per scenario (a breaker test wants
/// a low `MinimumThroughput`, a timeout test wants a short `Timeout.Duration`), which the production
/// `Program.cs`'s one shared, configuration-bound instance cannot offer per test.
/// </summary>
internal static class WebhookDispatchTestHarness
{
    /// <summary>Matches <see cref="Ago.Chat.FakeCrm.Tests.FakeCrmProcessFixture.SigningSecret"/> - the
    /// plaintext secret this dispatcher signs with must be the exact value the real running
    /// `Ago.Chat.FakeCrm` process verifies against, or every delivery in every test here would fail
    /// signature verification regardless of which personality it hit.</summary>
    public const string EndpointSecret = "fake-crm-tests-only-signing-secret";

    private const string CipherKey = "Vg1G2KjonUB1uH8trETJzr30EPoeqt0YRGzYibDKy1o=";

    public static (DispatchWebhooksForEventHandler Handler, WebhookResiliencePipelines Pipelines) CreateHandler(
        AgoChatDbContext db, ResiliencePipelineOptions resilienceOptions, WebhookHttpOptions httpOptions)
    {
        var pipelines = new WebhookResiliencePipelines(resilienceOptions);
        var httpClient = new HttpClient(new SocketsHttpHandler { ConnectTimeout = httpOptions.ConnectTimeout });
        var client = new HttpWebhookDeliveryClient(
            httpClient, pipelines, resilienceOptions, Options.Create(httpOptions), new SystemClock(),
            NullLogger<HttpWebhookDeliveryClient>.Instance);
        var handler = new DispatchWebhooksForEventHandler(
            new WebhookEndpointRepository(db), new WebhookDeliveryRepository(db), CreateCipher(),
            client, new UuidV7Generator(), new SystemClock());
        return (handler, pipelines);
    }

    public static WebhookSecretCipher CreateCipher() => new(new WebhookSecretCipherOptions { SecretEncryptionKey = CipherKey });

    public static async Task<WebhookEndpoint> RegisterEndpointAsync(
        AgoChatDbContext db, SiteId siteId, Uri url, DateTimeOffset now, string secret = EndpointSecret)
    {
        var endpoint = WebhookEndpoint.Register(
            new WebhookEndpointId(Guid.NewGuid()), siteId, url, CreateCipher().Encrypt(secret), now);
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync();
        return endpoint;
    }

    /// <summary>Deliberately unmeasured/scenario-tuned, not "the production default" -
    /// `Program.cs`'s own `ConfigureResilienceDefaults` is that; every caller here picks numbers small
    /// enough to make its own scenario provable in seconds, not minutes (CLAUDE.md: measure or stay
    /// silent - these are fixtures, not a performance claim).</summary>
    public static ResiliencePipelineOptions ResilienceOptions(
        TimeSpan? timeout = null, int maxRetryAttempts = 2, TimeSpan? retryDelay = null,
        double failureRatio = 0.5, int minimumThroughput = 2, TimeSpan? samplingDuration = null, TimeSpan? breakDuration = null,
        int maxConcurrency = 4, int maxQueuedActions = 16) => new()
        {
            Timeout = new ResilienceTimeoutOptions { Duration = timeout ?? TimeSpan.FromSeconds(2) },
            Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = retryDelay ?? TimeSpan.FromMilliseconds(50),
                BackoffType = DelayBackoffType.Exponential,
            },
            CircuitBreaker = new ResilienceCircuitBreakerOptions
            {
                FailureRatio = failureRatio,
                MinimumThroughput = minimumThroughput,
                SamplingDuration = samplingDuration ?? TimeSpan.FromSeconds(30),
                BreakDuration = breakDuration ?? TimeSpan.FromSeconds(30),
            },
            Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = maxConcurrency, MaxQueuedActions = maxQueuedActions },
        };

    public static WebhookHttpOptions HttpOptions(TimeSpan? connectTimeout = null, TimeSpan? responseHeadersTimeout = null) => new()
    {
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1),
        ResponseHeadersTimeout = responseHeadersTimeout ?? TimeSpan.FromSeconds(1),
    };

    public static async Task<SiteId> SeedSiteAsync(AgoChatDbContext db, SiteId? siteId = null)
    {
        var id = siteId ?? new SiteId(Guid.NewGuid());
        db.Sites.Add(new Site(id, $"site_{id.Value:N}", []));
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>
    /// A minimal DI container for the real consumer classes (`ConversationAssignmentWebhookDispatchConsumer`/
    /// `ConversationClosedWebhookDispatchConsumer`), which resolve `DispatchWebhooksForEventHandler`
    /// (and, for the closed-conversation one, `IConversationRepository`) through
    /// `IServiceScopeFactory.CreateAsyncScope()` per message - the same "no ASP.NET Core host, build
    /// only what the class under test actually resolves" shape <see cref="CreateHandler"/> uses for the
    /// handler alone, extended here because a consumer needs a real scope factory to construct one.
    /// </summary>
    public static (ServiceProvider Services, WebhookResiliencePipelines Pipelines) BuildConsumerServices(
        WebhookDispatchFixture fixture, ResiliencePipelineOptions resilienceOptions, WebhookHttpOptions httpOptions)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(o => o.UseNpgsql(fixture.DataSource));
        services.AddScoped<IWebhookEndpointRepository, WebhookEndpointRepository>();
        services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IWebhookSecretCipher>(_ => CreateCipher());

        var pipelines = new WebhookResiliencePipelines(resilienceOptions);
        services.AddSingleton(pipelines);
        services.AddSingleton<IWebhookDeliveryClient>(_ => new HttpWebhookDeliveryClient(
            new HttpClient(new SocketsHttpHandler { ConnectTimeout = httpOptions.ConnectTimeout }),
            pipelines, resilienceOptions, Options.Create(httpOptions), new SystemClock(), NullLogger<HttpWebhookDeliveryClient>.Instance));
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<DispatchWebhooksForEventHandler>();

        return (services.BuildServiceProvider(), pipelines);
    }
}
