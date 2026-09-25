using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.NotifyOperatorDevices;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Fcm;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.MaxBot;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Infrastructure.RuStore;
using Ago.Chat.Infrastructure.Telegram;
using Ago.Chat.Module;
using Ago.Chat.Module.Pipeline;
using Ago.Chat.Module.Push;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Observability;
using Ago.Platform.Kernel;
using Ago.Platform.Resilience;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Exporter;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// `7-01`: see Ago.Chat.Api's own remarks - one call per host, this host's own name.
builder.Services.AddPlatformObservability(builder.Configuration, "Ago.Chat.Worker");

new ChatModule().ConfigureServices(builder.Services, builder.Configuration);

builder.Services
    .AddOptions<OutboxDispatcherOptions>()
    .Bind(builder.Configuration.GetSection(OutboxDispatcherOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OutboxDispatcher>();

builder.Services
    .AddOptions<UnreadCounterConsumerOptions>()
    .Bind(builder.Configuration.GetSection(UnreadCounterConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<UnreadCounterConsumer>();

// `14-04`: a third Competing consumer of MessageAccepted, next to the two above - see
// OfflineAutoReplyConsumer's own remarks on why it is a consumer rather than part of the send path.
builder.Services
    .AddOptions<OfflineAutoReplyConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OfflineAutoReplyConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OfflineAutoReplyConsumer>();

// `20-07`: a fourth Competing consumer of MessageAccepted - see ModuleTaskConsumer's own remarks on
// why this is a separate consumer rather than a fifth branch inside OfflineAutoReplyConsumer above.
builder.Services
    .AddOptions<ModuleTaskConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ModuleTaskConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ModuleTaskConsumer>();

// `14-12`/`adr/0079`: a fifth Competing consumer of MessageAccepted - see LinkIdentityCommandConsumer's
// own remarks on why this is a separate consumer rather than a branch inside ModuleTaskConsumer above.
builder.Services
    .AddOptions<LinkIdentityCommandConsumerOptions>()
    .Bind(builder.Configuration.GetSection(LinkIdentityCommandConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<LinkIdentityCommandConsumer>();

// `14-02`: the outbound half of `14-01`'s port - see ChannelMessageDeliveryConsumer's own remarks.
builder.Services
    .AddOptions<ChannelMessageDeliveryConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ChannelMessageDeliveryConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ChannelMessageDeliveryConsumer>();

// `14-15`: the outbound half of phone verification - see PhoneVerificationDeliveryConsumer's own
// remarks for why this Worker consumer, not the initiating HTTP request, places the paid SMS/voice send.
builder.Services
    .AddOptions<PhoneVerificationDeliveryConsumerOptions>()
    .Bind(builder.Configuration.GetSection(PhoneVerificationDeliveryConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<PhoneVerificationDeliveryConsumer>();

// `14-02`: the dev-only inbound mechanism (MaxLongPollingService's own remarks on why both this and
// Ago.Chat.Api's webhook receiver exist). Ago.Chat.Worker, not Ago.Chat.Api, because this is
// restart-tolerant background work with no request to answer - adr/0013's own failure-profile split,
// applied the way this item's backlog note asks.
builder.Services.AddHostedService<MaxLongPollingService>();

// `14-07`: Telegram's own (only) inbound mechanism - see TelegramLongPollingService's own remarks on
// why this channel has no separate webhook receiver to also register, unlike MAX above. Ago.Chat.Worker
// for the identical adr/0013 failure-profile reason MaxLongPollingService's own comment states.
builder.Services.AddHostedService<TelegramLongPollingService>();

// Found live, 2026-08-28, verifying 14-02 against a real MAX bot: a message received here reaches
// ReceiveChannelMessageHandler -> SendVisitorMessageHandler -> IMessagePipeline.EnqueueAsync exactly
// the same way a widget message does (ReceiveChannelMessageHandler's own doc comment: "the code path
// a widget message already takes, unchanged") - but nothing in this host ever drained that pipeline.
// Ago.Chat.Api's own Program.cs registers ConversationSequencer/BatchAccumulator/MessageBatchWriter/
// MessagePipelineWorkerHost/BatchFlusherService on the explicit assumption "only Ago.Chat.Api's hubs
// ever enqueue onto it" (that comment, now stale) - true before 14-02 gave this host its own
// producer. The item's own conversation-was-created-but-the-message-never-landed symptom (silent: no
// exception, because EnqueueAsync's caller awaits an ack nothing ever completes) is exactly what a
// missing drainer looks like. Same five lines Ago.Chat.Api registers, because the classes themselves
// have no Api-specific dependency (verified by reading each one) - only which host runs them differs.
builder.Services.AddSingleton<ConversationSequencer>();
builder.Services.AddSingleton<BatchAccumulator>();
builder.Services.AddSingleton<MessageBatchWriter>();
builder.Services.AddHostedService<MessagePipelineWorkerHost>();
builder.Services.AddHostedService<BatchFlusherService>();

// `15-09`/`adr/0087`: PartitionMaintenanceJob, MessageSearchIndexJob and MessageSiteIdBackfillJob are
// all deleted in this change. `messages` no longer has a growing partition grid to maintain ahead of
// need (64 fixed hash buckets, created once by Stage15RepartitionMessagesByTenantHash and never
// again - PartitionMaintenanceJob's whole job); its search indexes are built the same way, once, in
// that same migration - a `CREATE INDEX CONCURRENTLY` per bucket, 64 times over, since Postgres refuses
// that statement directly against a partitioned parent (MessageSearchIndexJob's own removal note in
// that migration's doc comment has the full reasoning and the correction); and `site_id` is `NOT NULL`
// everywhere as of the same migration, closed once during its create-copy-drop rather than converged on
// slowly forever (`Message.SiteId`'s own remarks - MessageSiteIdBackfillJob's entire reason to exist).

builder.Services
    .AddOptions<ConnectionFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConnectionFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConnectionFanoutConsumer>();

// `23-32`: the team chat's own fan-out consumer - same registration shape as ConnectionFanoutConsumer
// right above.
builder.Services
    .AddOptions<TeamChatFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(TeamChatFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<TeamChatFanoutConsumer>();

// `23-33`: the team chat's own removal fan-out consumer - a distinct BackgroundService, not a second
// branch inside TeamChatFanoutConsumer above (that class's own remarks explain why).
builder.Services
    .AddOptions<TeamMessageRemovedFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(TeamMessageRemovedFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<TeamMessageRemovedFanoutConsumer>();

builder.Services
    .AddOptions<ConversationAssignmentFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConversationAssignmentFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationAssignmentFanoutConsumer>();

// `25-110`: the attachment-upload grant/revoke's own live-push fan-out consumer - same registration
// shape as ConversationAssignmentFanoutConsumer right above.
builder.Services
    .AddOptions<AttachmentUploadGrantFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentUploadGrantFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AttachmentUploadGrantFanoutConsumer>();

// `25-119`: the widget's own delivery-ack fan-out consumer - same registration shape as
// AttachmentUploadGrantFanoutConsumer right above.
builder.Services
    .AddOptions<MessageDeliveredFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(MessageDeliveredFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<MessageDeliveredFanoutConsumer>();

// 4-03: which mechanism actually performs the claim - concurrency.md's "two mechanisms, both
// implemented, compared" - chosen once at startup, not per-request. SkipLocked is the default
// (concurrency.md: "no extra infrastructure, no lock-lease expiry problems").
var assignmentMechanism = builder.Configuration["AssignmentEngine:Mechanism"] ?? "SkipLocked";
builder.Services.AddSingleton<IAssignmentClaimer>(sp => assignmentMechanism switch
{
    "SkipLocked" => new SkipLockedAssignmentClaimer(
        sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IIdGenerator>()),
    "RedisLock" => new RedisLockAssignmentClaimer(
        sp.GetRequiredService<RedisDistributedLock>(), sp.GetRequiredService<NpgsqlDataSource>(),
        sp.GetRequiredService<IClock>(), sp.GetRequiredService<IIdGenerator>()),
    _ => throw new InvalidOperationException(
        $"Unknown AssignmentEngine:Mechanism '{assignmentMechanism}' - expected 'SkipLocked' or 'RedisLock'."),
});

builder.Services
    .AddOptions<ConversationAssignmentJobOptions>()
    .Bind(builder.Configuration.GetSection(ConversationAssignmentJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationAssignmentJob>();

// 4-04: OperatorConversationReleaser is stateless beyond the shared NpgsqlDataSource pool, matching
// SkipLockedAssignmentClaimer/RedisLockAssignmentClaimer's own registration shape.
builder.Services.AddSingleton<OperatorConversationReleaser>();
// `26-03`: OperatorDeviceRevoker is the identical shape, for the identical reason.
builder.Services.AddSingleton<OperatorDeviceRevoker>();

// `26-04`/`adr/0180`/`26-100`/`adr/0181`: operator-push registration - deliberately here, in
// Ago.Chat.Worker's own Program.cs, and not in ChatModule.ConfigureServices (which Ago.Chat.Api and
// Ago.Chat.Webhooks call too). Both providers' credentials - RuStore's static bearer and FCM's
// service-account key - are the credentials `secrets.md` says must reach exactly one deployable, so
// binding either with .ValidateOnStart() from the shared method would make the other two hosts' startup
// depend on values they must never hold. FCM (`adr/0181`) is the primary transport, RuStore the fallback;
// there are now genuinely two providers, so IPushSenderResolver (`adr/0179` §5's dispatch table, no longer
// premature) maps each device's Provider to its own ResilientPushSender, and NotifyOperatorDevicesHandler
// selects per device. Each sender wraps its own PushResiliencePipeline instance so one provider's outage
// trips only its own breaker.
builder.Services
    .AddOptions<RuStoreOptions>()
    .Bind(builder.Configuration.GetSection(RuStoreOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ProjectId), "Push:RuStore:ProjectId must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceToken), "Push:RuStore:ServiceToken must be set.")
    .ValidateOnStart();
builder.Services.AddHttpClient<RuStorePushSender>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<RuStoreOptions>>().Value;
    var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
    // `adr/0180` §1: one host, one long-lived bearer token, presented directly - no OAuth2 mint, so
    // this is the whole of the client's setup, set once here at the composition root the same
    // "ChatModule builds the HttpClient, the client class stays thin" split YooKassaPaymentsApiClient's
    // own remarks describe for its own Basic-auth header.
    client.BaseAddress = new Uri($"{baseUrl}v1/projects/{options.ProjectId}/");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
});

// `26-100`/`adr/0181`: FCM (primary transport). ProjectId (ago-chat-783f7) is a public identifier;
// ServiceAccountJson is the one real secret (FCM_SERVICE_ACCOUNT_JSON), validated on start so the Worker
// refuses to boot without it rather than silently failing every FCM send at runtime.
builder.Services
    .AddOptions<FcmOptions>()
    .Bind(builder.Configuration.GetSection(FcmOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ProjectId), "Push:Fcm:ProjectId must be set.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceAccountJson), "Push:Fcm:ServiceAccountJson must be set.")
    .ValidateOnStart();
// The token-endpoint client and the send client are separate: the mint talks to Google's OAuth2 host,
// the send talks to fcm.googleapis.com. The access token is short-lived and minted per send by the
// singleton token provider (its cache shared across sends), so - unlike RuStore's static bearer above -
// no Authorization header is set on the send client here; FcmPushSender sets it per request.
builder.Services.AddHttpClient(FcmServiceAccountTokenProvider.HttpClientName);
builder.Services.AddSingleton<IFcmAccessTokenProvider>(sp => new FcmServiceAccountTokenProvider(
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<FcmOptions>>()));
builder.Services.AddHttpClient<FcmPushSender>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<FcmOptions>>().Value;
    var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
    client.BaseAddress = new Uri($"{baseUrl}v1/projects/{options.ProjectId}/");
});

// Starting points, not measured numbers - the identical caveat every resilience default in this
// codebase carries (CLAUDE.md rule 7). Close to Ago.Chat.Module.Channels.ChannelResiliencePipelines'
// own channel-send defaults, because the boundary is the same kind of thing - an HTTP call to a third
// party this codebase does not control - with a smaller bulkhead: a push fan-out sends at most one
// message per operator device per event, nowhere near a channel adapter's own send volume.
builder.Services.AddResiliencePipelineOptions(
    PushResiliencePipeline.PipelineName,
    builder.Configuration,
    options =>
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
        options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 4, MaxQueuedActions = 16 };
    });
// One PushResiliencePipeline instance per provider, keyed by the enum, so an FCM outage trips only FCM's
// breaker and RuStore fallback sends keep flowing (and vice versa) - the per-provider breaker isolation
// ChannelResiliencePipelines already keys per ChannelKind for the identical reason. Both instances read
// the same `Resilience:Push:*` thresholds (the boundary is the same kind of thing); only the state
// differs. Keyed singletons, never scoped: a scoped lifetime would rebuild a fresh, un-tripped breaker
// per DI scope, the failure every resilience-pipeline registration in this codebase warns against.
builder.Services.AddKeyedSingleton<PushResiliencePipeline>(PushProvider.RuStore, (sp, _) => new PushResiliencePipeline(
    sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(PushResiliencePipeline.PipelineName)));
builder.Services.AddKeyedSingleton<PushResiliencePipeline>(PushProvider.Fcm, (sp, _) => new PushResiliencePipeline(
    sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(PushResiliencePipeline.PipelineName)));

// The dispatch table itself: each provider to its own resilience-wrapped adapter. The thin adapters
// (RuStorePushSender/FcmPushSender) are per-scope typed HttpClients; the stateful breaker lives in the
// keyed singleton each is wrapped with.
builder.Services.AddScoped<IPushSenderResolver>(sp => new PushSenderResolver(
    new Dictionary<PushProvider, IPushSender>
    {
        [PushProvider.RuStore] = new ResilientPushSender(
            sp.GetRequiredService<RuStorePushSender>(),
            sp.GetRequiredKeyedService<PushResiliencePipeline>(PushProvider.RuStore)),
        [PushProvider.Fcm] = new ResilientPushSender(
            sp.GetRequiredService<FcmPushSender>(),
            sp.GetRequiredKeyedService<PushResiliencePipeline>(PushProvider.Fcm)),
    }));

// `26-05`/`push-notifications.md`'s own "Fan-out": NotifyOperatorDevicesHandler's own registration -
// deliberately here, next to IPushSender, and not in ChatModule.ConfigureServices (which Ago.Chat.Api
// and Ago.Chat.Webhooks call too) for the identical reason IPushSender itself lives here rather than
// there - see that registration's own remarks, and ChatModule.ConfigureServices' own remarks at the
// point this handler is conspicuously absent from its neighbours.
builder.Services.AddScoped<NotifyOperatorDevicesHandler>();

// `26-120`: fix C's safety-net dedup option, bound here alongside its only consumer (the handler above)
// for the same reason the handler itself is registered here and not in ChatModule - the plain-value,
// not-IOptions<T> shape MessageSendRateLimitOptions/OperatorPresenceLostSuppressionOptions already use.
// IRateLimiter the handler now also takes is already registered for every host by ChatModule's own
// AddRedisCaching, so nothing new to wire for it. `.Validate` keeps the TTL a genuinely validated
// option (a zero/negative TTL would make RefillPerSecond infinite/negative - never a valid claim window).
builder.Services
    .AddOptions<OperatorPushDedupOptions>()
    .Bind(builder.Configuration.GetSection(OperatorPushDedupOptions.SectionName))
    .Validate(o => o.Ttl > TimeSpan.Zero, "OperatorPushDedup:Ttl must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OperatorPushDedupOptions>>().Value);

// `26-05`/`push-notifications.md`'s own "Fan-out": the two consumers that actually call IPushSender -
// see each class's own remarks for its queue/DLQ naming and why a per-subscriber ConsumerName is what
// makes OperatorMessagePushConsumer a safe new subscriber on an already-crowded MessageAccepted topic.
builder.Services
    .AddOptions<OperatorAssignmentPushConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OperatorAssignmentPushConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorAssignmentPushConsumer>();

builder.Services
    .AddOptions<OperatorMessagePushConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OperatorMessagePushConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorMessagePushConsumer>();

// `26-86`: the third - see its own remarks for why it gets a solo new topic rather than crowding onto
// either sibling's own.
builder.Services
    .AddOptions<OperatorWaitingPushConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OperatorWaitingPushConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorWaitingPushConsumer>();

builder.Services
    .AddOptions<OperatorDisconnectGraceConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OperatorDisconnectGraceConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorDisconnectGraceConsumer>();

builder.Services
    .AddOptions<OperatorDisconnectSweepJobOptions>()
    .Bind(builder.Configuration.GetSection(OperatorDisconnectSweepJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorDisconnectSweepJob>();

// `18-06`: an Assigned conversation nobody has touched inside its per-channel-kind inactivity window
// closes itself - AutoCloseConversationHandler (registered in ChatModule) is what this job actually
// calls, resolved per candidate from a fresh scope (the job's own remarks explain why).
builder.Services
    .AddOptions<AutoCloseInactiveConversationsJobOptions>()
    .Bind(builder.Configuration.GetSection(AutoCloseInactiveConversationsJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AutoCloseInactiveConversationsJob>();

// `19-02`/`adr/0078`'s kind 2: assigns each recently-closed, still-untagged conversation zero or more
// of its own site's existing tags - CategorizeConversationHandler (registered in ChatModule) is what
// this job actually calls, resolved per candidate from a fresh scope (the job's own remarks explain
// why).
builder.Services
    .AddOptions<ConversationCategorizationJobOptions>()
    .Bind(builder.Configuration.GetSection(ConversationCategorizationJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationCategorizationJob>();

builder.Services
    .AddOptions<SiteCacheInvalidationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(SiteCacheInvalidationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteCacheInvalidationConsumer>();

// `23-48`: the CORS-layer sibling registered right beside it - its own hosted service, its own
// options section, the same "one consumer, one event type" shape as the one just above.
builder.Services
    .AddOptions<SiteAllowedOriginsCacheInvalidationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(SiteAllowedOriginsCacheInvalidationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteAllowedOriginsCacheInvalidationConsumer>();

// `5-04`: AttachmentOptions itself is already bound by ChatModule (every host); this is just the
// thumbnail job's own dimensions/quality and the consumer's retry shape.
builder.Services
    .AddOptions<AttachmentThumbnailOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentThumbnailOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddScoped<AttachmentThumbnailGenerator>();
builder.Services
    .AddOptions<AttachmentThumbnailConsumerOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentThumbnailConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AttachmentThumbnailConsumer>();

// `25-160`: the sibling of the pair right above - SiteLogoOptions itself is already bound by
// ChatModule (every host, since the upload endpoint needs it too); this is just the validating
// consumer's own retry shape.
builder.Services.AddScoped<SiteLogoValidator>();
builder.Services
    .AddOptions<SiteLogoValidationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(SiteLogoValidationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteLogoValidationConsumer>();

// `23-76`: the dedup consumer - a second, independent subscription to the same AttachmentConfirmed
// event (see AttachmentDeduplicationConsumer's own remarks on why this is a sibling, not a change to
// the thumbnail consumer above).
builder.Services.AddScoped<AttachmentDeduplicator>();
builder.Services
    .AddOptions<AttachmentDeduplicationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentDeduplicationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AttachmentDeduplicationConsumer>();

// `8-07`/`adr/0058`: the demo tenant expiry sweep - the narrow erasure this item builds because
// `16-02` is scoped and unbuilt. Needs the same Keycloak admin credential Ago.Chat.Api holds, because
// removing a demo tenant means removing its identity-provider user too; see that host's own remarks on
// why neither registration lives in ChatModule.
builder.Services.AddKeycloakDemoIdentities(builder.Configuration);
builder.Services
    .AddOptions<DemoTenantExpiryJobOptions>()
    .Bind(builder.Configuration.GetSection(DemoTenantExpiryJobOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<DemoTenantExpiryJob>();

// `22-08`/`adr/0149` rule 1: the internal lease's own renewal loop - SuspensionLeaseOptions is bound
// once in ChatModule.ConfigureServices (shared by Ago.Chat.Api's own owner writes), so no separate
// AddOptions call is needed here, unlike DemoTenantExpiryJobOptions right above.
builder.Services.AddHostedService<SuspensionLeaseRenewalJob>();

// `13-03`: the recurring monthly re-charge, retry/lapse machinery, and the operator-removal
// conversation release - see each type's own remarks.
builder.Services
    .AddOptions<SubscriptionRenewalJobOptions>()
    .Bind(builder.Configuration.GetSection(SubscriptionRenewalJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SubscriptionRenewalJob>();

builder.Services
    .AddOptions<OperatorRemovedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(OperatorRemovedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OperatorRemovedConsumer>();

builder.Services
    .AddOptions<ModuleQuantityImpactComputedConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ModuleQuantityImpactComputedConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ModuleQuantityImpactComputedConsumer>();

builder.Services
    .AddOptions<AttachmentOrphanSweepJobOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentOrphanSweepJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AttachmentOrphanSweepJob>();

// `15-04`: the pruning mechanism - outbox/webhook_deliveries/inbox bounded-batch deletes past a
// configurable window, and messages partitions dropped past a configurable, archive-gated horizon.
// Same registration shape as every other job on this page.
builder.Services
    .AddOptions<OutboxPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(OutboxPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<OutboxPruneJob>();

builder.Services
    .AddOptions<WebhookDeliveryPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(WebhookDeliveryPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<WebhookDeliveryPruneJob>();

// `23-19`
builder.Services
    .AddOptions<ChannelDeliveryPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(ChannelDeliveryPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ChannelDeliveryPruneJob>();

builder.Services
    .AddOptions<InboxPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(InboxPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<InboxPruneJob>();

// `24-12`: the access record's own stated retention, enforced by something that runs - the item's
// own Done-when, and the same registration shape as every prune job on this page.
builder.Services
    .AddOptions<AccessRecordPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(AccessRecordPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AccessRecordPruneJob>();

// `23-11`: the contact-reveal record's own stated retention, the identical registration shape.
builder.Services
    .AddOptions<ContactRevealPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(ContactRevealPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ContactRevealPruneJob>();

builder.Services
    .AddOptions<MessagePartitionPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(MessagePartitionPruneJobOptions.SectionName))
    .ValidateDataAnnotations()
    // `13-08`: RetentionWindowMonthsByClass's own values are a dictionary, which
    // ValidateDataAnnotations does not recurse into - checked explicitly here for the same
    // "must be at least 1" guarantee RetentionHorizonMonths's own [Range] attribute already gives its
    // sibling property, so a misconfigured per-tier window fails at startup rather than producing a
    // cutoff in the past the first time a prune cycle runs.
    .Validate(
        o => o.RetentionWindowMonthsByClass.Values.All(months => months >= 1),
        "RetentionWindowMonthsByClass values must all be at least 1 - a window of 0 or less would make the most recently completed month a drop candidate immediately, the same guarantee RetentionHorizonMonths itself enforces.")
    .ValidateOnStart();
builder.Services.AddHostedService<MessagePartitionPruneJob>();

// `13-06`/`adr/0031`, mechanism reworked by `15-09`: writes the per-(site, class, period) archive
// MessageArchiveGate looks for and MessagePartitionPruneJob waits on before its own DELETE sweep runs.
// MessageArchiveJobOptions is bound both as IOptions<T> (MessageArchiveJob itself) and as a plain
// singleton value (MessageArchiveWriter), the identical shape SiteExportJobOptions/SiteExportArchiveWriter
// already establish just above for the same reason - MessageArchiveJob deliberately shares
// MessagePartitionPruneJobOptions (already bound above) rather than a second, independently-configurable
// horizon that could drift from the one the prune job actually removes against (MessageArchiveJob's own
// remarks).
builder.Services
    .AddOptions<MessageArchiveJobOptions>()
    .Bind(builder.Configuration.GetSection(MessageArchiveJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MessageArchiveJobOptions>>().Value);
builder.Services.AddSingleton<MessageArchiveWriter>();
builder.Services.AddHostedService<MessageArchiveJob>();

// `16-02`: the account/conversation erasure jobs. SiteErasureJob reuses the same
// IDemoIdentityProvisioner port DemoTenantExpiryJob already registers just below via
// AddKeycloakDemoIdentities (its own DeleteAsync is already fully generic - see that interface's own
// remarks on why it was reused as-is rather than renamed for this second caller). One real operational
// caveat this reuse carries: AddKeycloakDemoIdentities only requires KeycloakAdminOptions.BaseUrl/
// ClientSecret to be set when DemoTenantOptions.Enabled is true - but real (non-demo) site erasure is
// a permanent capability, not a demo-only one, so a deployment that disables the demo-tenant feature
// but still wants SiteErasureJob to actually remove Keycloak users must configure that credential
// anyway. Not fixed here (widening AddKeycloakDemoIdentities's validation gate is a shared,
// cross-feature change this item did not set out to make); flagged so it does not surprise the first
// deployment that erases a real tenant with the demo feature off.
// `24-09`: ConversationArchiveEraser needs the same options instance as a plain singleton value (not
// IOptions<T>) - the identical "unwrap once, inject the value" shape MessageArchiveWriter's own
// registration above already establishes, and for the same reason: it lives in ConversationErasureJob's
// own dependency graph, not behind a scope.
builder.Services
    .AddOptions<ConversationErasureJobOptions>()
    .Bind(builder.Configuration.GetSection(ConversationErasureJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ConversationErasureJobOptions>>().Value);
builder.Services.AddSingleton<ConversationArchiveEraser>();
builder.Services.AddHostedService<ConversationErasureJob>();

builder.Services
    .AddOptions<SiteErasureJobOptions>()
    .Bind(builder.Configuration.GetSection(SiteErasureJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteErasureJob>();

// `23-73`: the account-inactivity watchdog - warns, then requests 22-30's own real erasure mechanism
// for a site whose watchdog has gone fully silent. Registered here, not ChatModule, the same
// "internal Worker-only plumbing" shape SiteErasureJobOptions/SiteErasureJob just above already take -
// nothing outside this host ever resolves either type.
builder.Services
    .AddOptions<InactivityWatchdogJobOptions>()
    .Bind(builder.Configuration.GetSection(InactivityWatchdogJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<InactivityWatchdogJob>();

// `25-83`: the download-threshold soft-warning sweep - see DownloadThresholdWatchdogJob's own remarks
// for why this is a sweep and not an inline send from GetAttachmentDownloadUrlHandler. Registered here,
// not ChatModule, the identical "internal Worker-only plumbing" shape InactivityWatchdogJobOptions just
// above already takes.
builder.Services
    .AddOptions<DownloadThresholdWatchdogJobOptions>()
    .Bind(builder.Configuration.GetSection(DownloadThresholdWatchdogJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<DownloadThresholdWatchdogJob>();

// `25-170`: the role-capacity and channel-entitlement watchdog - see EntitlementWatchdogJob's own
// remarks for why this is one job doing two independent things per site, and why the cadence is fixed
// at one minute by the author's own design rather than measured. Registered here, not ChatModule, the
// identical "internal Worker-only plumbing" shape InactivityWatchdogJobOptions/DownloadThresholdWatchdogJobOptions
// just above already take.
builder.Services
    .AddOptions<EntitlementWatchdogJobOptions>()
    .Bind(builder.Configuration.GetSection(EntitlementWatchdogJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<EntitlementWatchdogJob>();

// `adr/0184` decision 2: a module took a booking with no chat origin and minted a person id locally -
// this consumer creates the Person in the account's registry under that id. Replaces `23-59`'s
// ContactCarryoverJob, which copied contacts the other way into a customer table that no longer exists.
builder.Services
    .AddOptions<PersonRegisteredConsumerOptions>()
    .Bind(builder.Configuration.GetSection(PersonRegisteredConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<PersonRegisteredConsumer>();

// `16-03`: tenant export. SiteExportJobOptions is bound both as IOptions<T> (SiteExportJob itself,
// the same shape SiteErasureJobOptions uses) and as a plain singleton value
// (SiteExportArchiveWriter, the same "plain value, not IOptions<T>" shape RegisterSiteRateLimitOptions
// establishes) - both consumers live in this singleton BackgroundService's own dependency graph, so
// both need a registration IFileStorage's own Singleton lifetime (Ago.Platform.Storage.S3) can satisfy
// without a scope.
builder.Services
    .AddOptions<SiteExportJobOptions>()
    .Bind(builder.Configuration.GetSection(SiteExportJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SiteExportJobOptions>>().Value);
builder.Services.AddSingleton<SiteExportArchiveWriter>();
builder.Services.AddHostedService<SiteExportJob>();

// This item's own TTL sweep. No AddOptions<SiteExportPruneJobOptions>() call here, unlike every job
// right above - ChatModule.ConfigureServices already bound and validated it (that class's own remarks:
// GetSiteExportHistoryHandler, in Ago.Chat.Api, needs the identical bound instance this job reads its
// RetentionWindow from, so the bind lives where both hosts can reach it, once).
builder.Services.AddHostedService<SiteExportPruneJob>();

// Liveness stays trivial (the process is running); readiness now means "can actually reach the
// dependencies this dispatcher needs" (2-04), replacing 0-03's always-healthy stand-in.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

// `8-08`/`adr/0056`: run before anything can listen, and deliberately not as an IHostedService -
// GenericWebHostService opens the socket before any service registered after it, so a hosted service
// that threw would do so with requests already arriving. A host whose database is behind the
// migrations its own build carries refuses to start rather than serving 200s for pages whose queries
// fail; that is the 2026-08-25 incident, closed. It is also the whole of this system's deploy
// ordering: nothing orchestrates "migrator Job first", the hosts simply do not come up until it has
// run. See SchemaVersionGuard for why this beats an init container and where the expected version
// comes from.
await app.Services.EnsureSchemaIsCurrentAsync();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// `15-06`: see Ago.Chat.Api's own remarks on this line - the commit this binary was built from,
// readable from the running pod. All three hosts deploy from one commit, so a disagreement between
// them is a half-finished deploy that no single-host check can see.
app.MapGet("/healthz/version", () => BuildInfoResponse.For(typeof(Program).Assembly));

// `7-02` fix: see Ago.Chat.Api's own remarks on this line.
app.MapPrometheusScrapingEndpoint();

app.Run();
