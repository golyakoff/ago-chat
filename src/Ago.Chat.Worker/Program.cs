using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Module;
using Ago.Chat.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

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
    .AddOptions<SiteCacheInvalidationConsumerOptions>()
    .Bind(builder.Configuration.GetSection(SiteCacheInvalidationConsumerOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddHostedService<SiteCacheInvalidationConsumer>();

// Liveness stays trivial (the process is running); readiness now means "can actually reach the
// dependencies this dispatcher needs" (2-04), replacing 0-03's always-healthy stand-in.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();
