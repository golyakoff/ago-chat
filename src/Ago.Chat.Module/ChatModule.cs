using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Module.Pipeline;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Ago.Platform.Storage.S3;
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

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendVisitorMessageHandler>();
        services.AddScoped<SendOperatorMessageHandler>();
        services.AddScoped<GetConversationHistoryHandler>();
        services.AddScoped<GetSiteConfigByPublicKeyHandler>();
        services.AddScoped<GetSiteConfigByIdHandler>();
        services.AddScoped<CheckCorsOriginHandler>();
        services.AddScoped<AssignConversationHandler>();
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

        // `6-03`: the registration and delivery-history backend for a future self-service console
        // screen - see each handler's own remarks. Registered for every host (the same shape as
        // everything else on this page) even though only `Ago.Chat.Api` maps HTTP endpoints for them
        // today; `6-05`'s dispatcher, a `Ago.Chat.Webhooks` consumer, is what will eventually resolve
        // `IWebhookEndpointRepository`/`IWebhookDeliveryRepository` from that same host.
        services.AddScoped<RegisterWebhookEndpointHandler>();
        services.AddScoped<ListWebhookEndpointsHandler>();
        services.AddScoped<RevokeWebhookEndpointHandler>();
        services.AddScoped<GetWebhookDeliveriesHandler>();

        // 4-04: needed by both hosts - Ago.Chat.Api's OperatorHub (the query-at-disconnect fast
        // path) and Ago.Chat.Worker's OperatorDisconnectSweepJob (the periodic backstop).
        services.AddSingleton<OperatorPresencePublisher>();
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
}
