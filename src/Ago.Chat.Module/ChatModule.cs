using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        // Registered for every host (matching AddRabbitMqMessaging's own shape) but only ever
        // resolved - and only then does it open a Redis connection - by Ago.Chat.Api, which is the
        // only host holding SignalR connections (3-01). Worker/Webhooks never trigger it.
        services.AddConnectionRegistry(configuration);

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendVisitorMessageHandler>();
        services.AddScoped<SendOperatorMessageHandler>();
        services.AddScoped<GetConversationHistoryHandler>();
        services.AddScoped<AssignConversationHandler>();
        services.AddScoped<RecordUnreadMessageHandler>();
    }
}
