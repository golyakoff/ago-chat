using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendVisitorMessageHandler>();
        services.AddScoped<SendOperatorMessageHandler>();
        services.AddScoped<GetConversationHistoryHandler>();
        services.AddScoped<GetSiteConfigByPublicKeyHandler>();
        services.AddScoped<AssignConversationHandler>();
        services.AddScoped<RecordUnreadMessageHandler>();
        services.AddScoped<ResolveMessageDeliveryTargetsHandler>();
        services.AddScoped<ResolveConversationAssignmentTargetsHandler>();

        // 4-04: needed by both hosts - Ago.Chat.Api's OperatorHub (the query-at-disconnect fast
        // path) and Ago.Chat.Worker's OperatorDisconnectSweepJob (the periodic backstop).
        services.AddSingleton<OperatorPresencePublisher>();
    }
}
