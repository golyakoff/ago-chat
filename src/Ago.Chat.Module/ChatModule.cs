using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Application.UseCases.DeliverChannelMessage;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetOfflineAutoReply;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.ListMyTenancies;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Infrastructure.MaxBot;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Module.Channels;
using Ago.Chat.Module.Pipeline;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Ago.Platform.Resilience;
using Ago.Platform.Storage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;

namespace Ago.Chat.Module;

/// <summary>
/// The one <see cref="IProductModule"/> every AGO Chat host loads
/// (docs/architecture/clean-architecture.md). Registers the DI services every Stage 1 handler and
/// port needs (`1-06`); the request-pipeline wiring (hubs, endpoints, auth middleware) stays in each
/// host's own <c>Program.cs</c>, since <see cref="IProductModule"/> has no hook for it today -
/// growing one is deferred until a second host genuinely needs it (Worker/Webhooks consumers, or a
/// second product), rather than guessed at from one caller (clean-architecture.md).
/// </summary>
public sealed class ChatModule : IProductModule
{
    public string Name => "Ago.Chat";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = Environment.GetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set AGO_CHAT_CONNECTION_STRING - e.g. the docker-compose Postgres from local-dev.md.");
        services.AddPostgresPersistence(connectionString);
        // `8-08`: bound here, with every other options group in this product - AddPostgresPersistence
        // takes a connection string rather than an IConfiguration. Registered for every host because
        // every serving host runs the guard (adr/0056); Ago.Chat.Migrator does not use ChatModule at
        // all, and does not need this - it is the thing the guard waits for.
        services
            .AddOptions<SchemaGuardOptions>()
            .Bind(configuration.GetSection(SchemaGuardOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddPlatformKernel();
        services.AddRabbitMqMessaging(configuration);
        // Registered for every host (matching AddRabbitMqMessaging's own shape). Ago.Chat.Api
        // resolves it for its own connection lifecycle (3-01, the only host holding SignalR
        // connections); Ago.Chat.Worker resolves it too as of 4-04 - OperatorDisconnectGraceConsumer/
        // OperatorDisconnectSweepJob read presence to decide whether an operator's conversations
        // should be released, without themselves ever holding a connection.
        services.AddConnectionRegistry(configuration);
        // Same "registered everywhere, resolved where it matters" shape: Ago.Chat.Api resolves
        // ICache (the site-config read) and CacheInvalidationConsumer; Ago.Chat.Worker resolves only
        // CacheInvalidationPublisher (via SiteCacheInvalidationConsumer) - TryAddSingleton means
        // whichever of AddConnectionRegistry/AddRedisCaching a host wires up first opens the one
        // Redis connection the other reuses (3-04).
        services.AddRedisCaching(configuration);

        // 3-05: bound here (not Ago.Chat.Api's Program.cs) because SendVisitorMessageHandler, the
        // only consumer, lives in Application and is registered for every host - the handler itself
        // takes the plain MessageSendRateLimitOptions value, never IOptions<T> (see the handler's
        // own remarks), so this factory is the one place that unwraps it.
        services
            .AddOptions<MessageSendRateLimitOptions>()
            .Bind(configuration.GetSection(MessageSendRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MessageSendRateLimitOptions>>().Value);

        // `5-03`: the platform's presigned-upload/download port (`5-02`) - registered here rather
        // than a host's own Program.cs for the same reason as everything else on this page:
        // CreateAttachmentHandler/ConfirmAttachmentHandler/GetAttachmentDownloadUrlHandler are
        // registered for every host below.
        services.AddS3FileStorage(configuration);

        // Same "bound here, plain-value-not-IOptions<T> handed to the handler" shape as
        // MessageSendRateLimitOptions above - CreateAttachmentHandler is the only consumer of either.
        services
            .AddOptions<AttachmentOptions>()
            .Bind(configuration.GetSection(AttachmentOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AttachmentOptions>>().Value);
        services
            .AddOptions<AttachmentRateLimitOptions>()
            .Bind(configuration.GetSection(AttachmentRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AttachmentRateLimitOptions>>().Value);

        // `10-02`: bound here, not Ago.Chat.Api's Program.cs - RegisterSiteHandler is registered for
        // every host below, the same MessageSendRateLimitOptions/AttachmentRateLimitOptions shape.
        services
            .AddOptions<RegisterSiteRateLimitOptions>()
            .Bind(configuration.GetSection(RegisterSiteRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RegisterSiteRateLimitOptions>>().Value);

        // `6-03`: bound here, not a host's own Program.cs - RegisterWebhookEndpointHandler is
        // registered for every host below, the same MessageSendRateLimitOptions/AttachmentOptions
        // shape. Deliberately no random-per-process fallback (WebhookSecretCipherOptions' own remarks)
        // - Validate()+ValidateOnStart() is what turns a missing/malformed key into a startup failure
        // instead of a silent, unrecoverable loss the first time a secret is encrypted.
        services
            .AddOptions<WebhookSecretCipherOptions>()
            .Bind(configuration.GetSection(WebhookSecretCipherOptions.SectionName))
            .Validate(IsValidBase64Aes256Key, "Webhooks:SecretEncryptionKey must be a base64-encoded 32-byte AES-256 key.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<WebhookSecretCipherOptions>>().Value);

        // 4-05: bound and registered here, not Ago.Chat.Api's Program.cs - the same DI-validation
        // reason as OperatorPresencePublisher (4-04), see ChannelMessagePipeline's own remarks.
        // SendVisitorMessageHandler/SendOperatorMessageHandler are registered for every host below
        // and now depend on IMessagePipeline, so an implementation must be resolvable everywhere
        // even though only Ago.Chat.Api's MessagePipelineWorkerHost/BatchFlusherService (registered
        // in its own Program.cs) ever actually drain it.
        services
            .AddOptions<MessagePipelineOptions>()
            .Bind(configuration.GetSection(MessagePipelineOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<ChannelMessagePipeline>();
        services.AddSingleton<IMessagePipeline>(sp => sp.GetRequiredService<ChannelMessagePipeline>());

        // `14-01`: AGO Inbox's channel seam. Registered for every host, the same shape as everything
        // else on this page - which host actually runs a channel adapter is `14-02`/`14-03`'s decision
        // (a webhook receiver and a long-polling worker are both plausible), and a host that registers
        // no IInboundChannelAdapter simply gets an empty registry rather than a startup failure.
        services.AddResiliencePipelineOptions(
            ChannelResiliencePipelines.PipelineName, configuration, ConfigureChannelResilienceDefaults);
        // Constructed through a factory that resolves the *named* options group itself, rather than
        // registering a bare ResiliencePipelineOptions singleton the way Ago.Chat.Webhooks' Program.cs
        // does for its own group. That matters here and not there: ChatModule runs inside every host,
        // including Ago.Chat.Webhooks, so a second unnamed ResiliencePipelineOptions registration would
        // put two registrations of one type in that host's container and leave which pipeline gets
        // which thresholds decided by registration order.
        services.AddSingleton(sp => new ChannelResiliencePipelines(
            sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>()
                .Get(ChannelResiliencePipelines.PipelineName)));
        services.AddSingleton<IInboundChannelAdapterRegistry, InboundChannelAdapterRegistry>();
        services.AddScoped<ReceiveChannelMessageHandler>();
        // `14-02`: the outbound half - relays an operator's already-committed reply through whichever
        // channel the visitor was reached by. See DeliverChannelMessageHandler's own remarks for why it
        // is driven off MessageAccepted rather than the send path.
        services.AddScoped<DeliverChannelMessageHandler>();

        // `14-02`/`adr/0069`: bound here, with WebhookSecretCipherOptions right above it - the same
        // fail-fast-on-a-missing-key discipline, a different named section and a different key.
        services
            .AddOptions<ChannelCredentialCipherOptions>()
            .Bind(configuration.GetSection(ChannelCredentialCipherOptions.SectionName))
            .Validate(IsValidBase64Aes256Key, "Channels:CredentialEncryptionKey must be a base64-encoded 32-byte AES-256 key.")
            .ValidateOnStart();
        services.AddScoped<RegisterChannelCredentialHandler>();
        services.AddScoped<RevokeChannelCredentialHandler>();

        // `14-02`: MAX's own outbound client and adapter - registered here, for every host, the same
        // "registered everywhere, resolved where it matters" shape as everything else on this page.
        // MaxChannelAdapter is registered as itself first, then decorated into IInboundChannelAdapter -
        // ResilientInboundChannelAdapter's own remarks explain why composition beats a base class here.
        services
            .AddOptions<MaxBotApiOptions>()
            .Bind(configuration.GetSection(MaxBotApiOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<MaxLongPollingServiceOptions>()
            .Bind(configuration.GetSection(MaxLongPollingServiceOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient<MaxApiClient>((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<MaxBotApiOptions>>().Value.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        });
        // Singleton, not scoped: InboundChannelAdapterRegistry (below) is itself a singleton built from
        // IEnumerable<IInboundChannelAdapter>, so every adapter it can hold must be safe to keep for the
        // process lifetime - MaxChannelAdapter's own remarks explain how it reaches its Scoped
        // repositories anyway (IServiceScopeFactory, one scope per SendAsync call).
        services.AddSingleton<MaxChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<MaxChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendVisitorMessageHandler>();
        services.AddScoped<SendOperatorMessageHandler>();
        services.AddScoped<GetConversationHistoryHandler>();
        services.AddScoped<GetSiteConfigByPublicKeyHandler>();
        services.AddScoped<GetSiteConfigByIdHandler>();
        services.AddScoped<CheckCorsOriginHandler>();
        services.AddScoped<AssignConversationHandler>();
        // `4-06`: OperatorHub's own connect/disconnect wiring - see the handler's own remarks.
        services.AddScoped<SetOperatorPresenceHandler>();
        services.AddScoped<RecordUnreadMessageHandler>();
        services.AddScoped<ResolveMessageDeliveryTargetsHandler>();
        services.AddScoped<ResolveConversationAssignmentTargetsHandler>();
        services.AddScoped<CreateAttachmentHandler>();
        services.AddScoped<ConfirmAttachmentHandler>();
        services.AddScoped<GetAttachmentDownloadUrlHandler>();
        services.AddScoped<ResolveOperatorIdentityHandler>();
        // `5-07`: both minimal additions found missing while building the console - see each
        // handler's own remarks for what gap it closes.
        services.AddScoped<GetOperatorQueueHandler>();
        services.AddScoped<GetVisitorPresenceHandler>();
        // `5-08`: the admin role's own two new callers, plus the same kind of console-side gap
        // GetOperatorQueueHandler/GetVisitorPresenceHandler closed for `5-07` - see each handler's
        // own remarks.
        services.AddScoped<GetAllConversationsForSiteHandler>();
        services.AddScoped<DeleteAttachmentHandler>();
        services.AddScoped<GetMyPermissionsHandler>();
        // `6-02`: the first real caller of Conversation.Close() - see the handler's own remarks.
        services.AddScoped<CloseConversationHandler>();
        // `5-15`: the unread counter's first-ever downward writer - see the handler's own remarks.
        services.AddScoped<MarkConversationReadHandler>();
        // `10-02`
        services.AddScoped<RegisterSiteHandler>();
        // `13-07`/`adr/0068`: the console switcher's own read - see the handler's own remarks.
        services.AddScoped<ListMyTenanciesHandler>();
        // `12-02`: only Ago.Chat.Api ever resolves this one (it backs a single HTTP endpoint gated by
        // `12-01`'s owner policy), registered here for the same reason as everything else on this
        // page - ChatModule is where handler registration lives, and a host that never maps the route
        // simply never resolves it.
        services.AddScoped<ListSitesForOwnerHandler>();

        // `6-03`: the registration and delivery-history backend for a future self-service console
        // screen - see each handler's own remarks. Registered for every host (the same shape as
        // everything else on this page) even though only `Ago.Chat.Api` maps HTTP endpoints for them
        // today; `6-05`'s dispatcher, a `Ago.Chat.Webhooks` consumer, is what will eventually resolve
        // `IWebhookEndpointRepository`/`IWebhookDeliveryRepository` from that same host.
        services.AddScoped<RegisterWebhookEndpointHandler>();
        services.AddScoped<ListWebhookEndpointsHandler>();
        services.AddScoped<RevokeWebhookEndpointHandler>();
        services.AddScoped<GetWebhookDeliveriesHandler>();

        // `11-01`: Site's first real read/write handler pair since `1-04` - see each handler's own
        // remarks (GetWidgetConfigHandler deliberately uncached, UpdateWidgetConfigHandler the first
        // real SiteSettingsChanged producer). Registered for every host, the same shape as everything
        // else on this page, even though only `Ago.Chat.Api` maps HTTP endpoints for them today.
        services.AddScoped<GetWidgetConfigHandler>();
        services.AddScoped<UpdateWidgetConfigHandler>();

        // `14-04`: the offline auto-reply's three handlers. The read/write pair backs `Ago.Chat.Api`'s
        // own settings endpoints (the same `site:configure` gate `11-01`'s pair uses); the third is
        // resolved per message by `Ago.Chat.Worker`'s OfflineAutoReplyConsumer. Registered here for
        // every host, the same shape as everything else on this page - a host that maps no route and
        // runs no consumer simply never resolves them.
        services.AddScoped<GetOfflineAutoReplyHandler>();
        services.AddScoped<UpdateOfflineAutoReplyHandler>();
        services.AddScoped<SendOfflineAutoReplyHandler>();

        // 4-04: needed by both hosts - Ago.Chat.Api's OperatorHub (the query-at-disconnect fast
        // path) and Ago.Chat.Worker's OperatorDisconnectSweepJob (the periodic backstop).
        services.AddSingleton<OperatorPresencePublisher>();

        // `15-04`: registered for every host, the same shape as everything else on this page, even
        // though only Ago.Chat.Worker's MessagePartitionPruneJob resolves it today. AlwaysConfirmedMessageArchiveGate
        // is the stand-in until `13-06` exists - see that class's and IMessageArchiveGate's own remarks.
        services.AddSingleton<IMessageArchiveGate, AlwaysConfirmedMessageArchiveGate>();
    }

    /// <summary>
    /// `14-01`: starting points, not measured numbers - the same caveat
    /// <c>MessageSendRateLimitOptions</c> and `6-05`'s own <c>ConfigureResilienceDefaults</c> carry
    /// (CLAUDE.md: "do not invent numbers... measure or stay silent"). They are deliberately close to
    /// the webhook dispatcher's, because the boundary is the same kind of thing - an HTTP call to a
    /// third party we cannot fix - and copying a shape that has at least been exercised against a real
    /// hanging endpoint beats inventing a second set. `14-02` is the first item with a real provider to
    /// measure against, and is where these should stop being guesses.
    ///
    /// <para>Defaults exist at all, rather than leaving every group null, because a null group builds a
    /// pipeline with no strategies: an adapter author who forgot to add a
    /// <c>Resilience:Channels</c> section would get an unprotected call to a third party and no
    /// signal. Configuration still overrides any key actually present
    /// (<c>AddResiliencePipelineOptions</c>' own remarks).</para>
    /// </summary>
    private static void ConfigureChannelResilienceDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(5) };
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
        };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(10),
        };
        options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 8, MaxQueuedActions = 32 };
    }

    // `6-03`: a plain boolean predicate rather than throwing inside the lambda - `.Validate()` expects
    // one, and the actual "why" (must be base64, must decode to exactly 32 bytes for AES-256) is
    // already stated in the message passed alongside it and in WebhookSecretCipherOptions' own remarks.
    private static bool IsValidBase64Aes256Key(WebhookSecretCipherOptions options)
    {
        try
        {
            return Convert.FromBase64String(options.SecretEncryptionKey).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // `14-02`/`adr/0069`: same check, ChannelCredentialCipherOptions' own key - kept as a second
    // overload rather than widened to `string` because `.Validate()` binds by the options type it is
    // chained onto, the same shape the overload above already has.
    private static bool IsValidBase64Aes256Key(ChannelCredentialCipherOptions options)
    {
        try
        {
            return Convert.FromBase64String(options.CredentialEncryptionKey).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
