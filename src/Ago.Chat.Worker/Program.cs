using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Chat.Infrastructure.MaxBot;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Infrastructure.Telegram;
using Ago.Chat.Module;
using Ago.Chat.Module.Pipeline;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Observability;
using Ago.Platform.Kernel;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Exporter;

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

// `14-02`: the outbound half of `14-01`'s port - see ChannelMessageDeliveryConsumer's own remarks.
builder.Services
    .AddOptions<ChannelMessageDeliveryConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ChannelMessageDeliveryConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ChannelMessageDeliveryConsumer>();

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

builder.Services
    .AddOptions<PartitionMaintenanceJobOptions>()
    .Bind(builder.Configuration.GetSection(PartitionMaintenanceJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<PartitionMaintenanceJob>();

builder.Services
    .AddOptions<ConnectionFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConnectionFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConnectionFanoutConsumer>();

builder.Services
    .AddOptions<ConversationAssignmentFanoutConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConversationAssignmentFanoutConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationAssignmentFanoutConsumer>();

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

builder.Services
    .AddOptions<SiteCacheInvalidationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(SiteCacheInvalidationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteCacheInvalidationConsumer>();

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

builder.Services
    .AddOptions<InboxPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(InboxPruneJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<InboxPruneJob>();

builder.Services
    .AddOptions<MessagePartitionPruneJobOptions>()
    .Bind(builder.Configuration.GetSection(MessagePartitionPruneJobOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<MessagePartitionPruneJob>();

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
builder.Services
    .AddOptions<ConversationErasureJobOptions>()
    .Bind(builder.Configuration.GetSection(ConversationErasureJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<ConversationErasureJob>();

builder.Services
    .AddOptions<SiteErasureJobOptions>()
    .Bind(builder.Configuration.GetSection(SiteErasureJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteErasureJob>();

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
