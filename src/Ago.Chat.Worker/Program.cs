using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Module;
using Ago.Chat.Worker;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
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

builder.Services
    .AddOptions<AttachmentOrphanSweepJobOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentOrphanSweepJobOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<AttachmentOrphanSweepJob>();

// Liveness stays trivial (the process is running); readiness now means "can actually reach the
// dependencies this dispatcher needs" (2-04), replacing 0-03's always-healthy stand-in.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// `7-02` fix: see Ago.Chat.Api's own remarks on this line.
app.MapPrometheusScrapingEndpoint();

app.Run();
