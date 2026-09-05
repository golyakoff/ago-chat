using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.AssignConversation;
using Ago.Chat.Application.UseCases.AutoCloseConversation;
using Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;
using Ago.Chat.Application.UseCases.CancelSubscription;
using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Application.UseCases.ChangeSubscriptionSeats;
using Ago.Chat.Application.UseCases.CheckCorsOrigin;
using Ago.Chat.Application.UseCases.CloseConversation;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.CreateCheckoutSession;
using Ago.Chat.Application.UseCases.AddConversationNote;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Application.UseCases.CreateTag;
using Ago.Chat.Application.UseCases.DeleteTag;
using Ago.Chat.Application.UseCases.GetConversationNotes;
using Ago.Chat.Application.UseCases.GetConversationTags;
using Ago.Chat.Application.UseCases.ListTags;
using Ago.Chat.Application.UseCases.RenameTag;
using Ago.Chat.Application.UseCases.TagConversation;
using Ago.Chat.Application.UseCases.UntagConversation;
using Ago.Chat.Application.UseCases.DeleteAttachment;
using Ago.Chat.Application.UseCases.DeliverChannelMessage;
using Ago.Chat.Application.UseCases.EnableModuleForSite;
using Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.GenerateReplyDraft;
using Ago.Chat.Application.UseCases.GetAllConversationsForSite;
using Ago.Chat.Application.UseCases.GetAttachmentDownloadUrl;
using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Application.UseCases.GetCannedResponses;
using Ago.Chat.Application.UseCases.GetConversationById;
using Ago.Chat.Application.UseCases.GetConversationOutcome;
using Ago.Chat.Application.UseCases.SetConversationOutcome;
using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.GetConversionReportForSite;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetOfflineAutoReply;
using Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;
using Ago.Chat.Application.UseCases.GetOperatorQueue;
using Ago.Chat.Application.UseCases.GetTagBreakdownReportForSite;
using Ago.Chat.Application.UseCases.GetSiteByPublicKey;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.GetSiteForOwner;
using Ago.Chat.Application.UseCases.GetSiteInstallation;
using Ago.Chat.Application.UseCases.GetOperatorTeam;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;
using Ago.Chat.Application.UseCases.GetSiteExportStatus;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.GetVisitorPresence;
using Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;
using Ago.Chat.Application.UseCases.ListModuleTaskChannelPriorityList;
using Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;
using Ago.Chat.Application.UseCases.GetWebhookDeliveries;
using Ago.Chat.Application.UseCases.GetWidgetConfig;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Application.UseCases.ListMessageArchives;
using Ago.Chat.Application.UseCases.ListMyTenancies;
using Ago.Chat.Application.UseCases.ListSitesForOwner;
using Ago.Chat.Application.UseCases.ListWebhookEndpoints;
using Ago.Chat.Application.UseCases.MarkConversationRead;
using Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;
using Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.RecordUnread;
using Ago.Chat.Application.UseCases.RedeemOperatorInvite;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Application.UseCases.RegisterWebhookEndpoint;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;
using Ago.Chat.Application.UseCases.RequestConversationErasure;
using Ago.Chat.Application.UseCases.RequestSiteErasure;
using Ago.Chat.Application.UseCases.RequestSiteExport;
using Ago.Chat.Application.UseCases.ResolveConversationAssignment;
using Ago.Chat.Application.UseCases.ResolveMessageDelivery;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Application.UseCases.RevokeModuleForSite;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.RotateModuleCredential;
using Ago.Chat.Application.UseCases.RouteConversationToModule;
using Ago.Chat.Application.UseCases.VerifyModuleRegistration;
using Ago.Chat.Application.UseCases.RevokeWebhookEndpoint;
using Ago.Chat.Application.UseCases.SearchConversations;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.SendOfflineAutoReply;
using Ago.Chat.Application.UseCases.SetModuleTaskChannelPriorityList;
using Ago.Chat.Application.UseCases.SetOperatorPresence;
using Ago.Chat.Application.UseCases.SetPreferredChannelIdentity;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Application.UseCases.TransferConversation;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentity;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner;
using Ago.Chat.Application.UseCases.UpdateCannedResponses;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;
using Ago.Chat.Application.UseCases.InitiatePhoneVerification;
using Ago.Chat.Application.UseCases.ConfirmPhoneVerification;
using Ago.Chat.Module.PhoneVerification;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;
using Ago.Chat.Infrastructure.MaxBot;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Infrastructure.Telegram;
using Ago.Chat.Infrastructure.Vk;
using Ago.Chat.Infrastructure.WhatsApp;
using Ago.Chat.Infrastructure.Email;
using Ago.Chat.Infrastructure.YandexGpt;
using Ago.Chat.Infrastructure.YooKassa;
using Ago.Chat.Module.Billing;
using Ago.Chat.Module.Categorization;
using Ago.Chat.Module.Channels;
using Ago.Chat.Module.Modules;
using Ago.Chat.Module.Pipeline;
using Ago.Chat.Module.ReplyDraft;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Hosting;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Realtime;
using Ago.Platform.Resilience;
using Ago.Platform.Storage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // `13-01`: bound here, not a host's own Program.cs - CreateOperatorInviteHandler is registered
        // for every host below, the same RegisterSiteRateLimitOptions shape (a plain value handed to
        // the handler, not IOptions<T> - see that class's own remarks).
        services
            .AddOptions<OperatorInviteOptions>()
            .Bind(configuration.GetSection(OperatorInviteOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OperatorInviteOptions>>().Value);

        // `16-03`: bound here, not a host's own Program.cs - RequestSiteExportHandler/
        // GetSiteExportStatusHandler are registered for every host below, the same
        // RegisterSiteRateLimitOptions shape (a plain value, not IOptions<T>).
        services
            .AddOptions<SiteExportRateLimitOptions>()
            .Bind(configuration.GetSection(SiteExportRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SiteExportRateLimitOptions>>().Value);
        services
            .AddOptions<SiteExportOptions>()
            .Bind(configuration.GetSection(SiteExportOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SiteExportOptions>>().Value);
        // `13-06`: the identical shape, one setting, for the retention-archive download read.
        services
            .AddOptions<MessageArchiveOptions>()
            .Bind(configuration.GetSection(MessageArchiveOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MessageArchiveOptions>>().Value);

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

        // `14-12`/`adr/0079`: verified channel-identity linking and unlinking - bound here, with
        // OperatorInviteOptions right below, the same shape. Both originators
        // (RequestChannelLinkFromConsoleHandler, HandleLinkIdentityCommandHandler) share this one
        // options group (PendingChannelLinkRequestOptions' own remarks on why).
        services
            .AddOptions<PendingChannelLinkRequestOptions>()
            .Bind(configuration.GetSection(PendingChannelLinkRequestOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PendingChannelLinkRequestOptions>>().Value);
        services.AddScoped<RequestChannelLinkFromConsoleHandler>();
        // Resolved by Ago.Chat.Worker's own LinkIdentityCommandConsumer, off MessageAccepted - the
        // identical shape RouteConversationToModuleHandler/SendOfflineAutoReplyHandler already establish.
        services.AddScoped<HandleLinkIdentityCommandHandler>();
        services.AddScoped<UnlinkChannelIdentityHandler>();
        // Resolved only by Ago.Chat.Api's own owner-scoped route - the identical "registered here like
        // every other handler, resolved by exactly one host" shape ListSitesForOwnerHandler's own
        // remarks describe for itself.
        services.AddScoped<UnlinkChannelIdentityAsOwnerHandler>();
        services.AddScoped<ListChannelIdentitiesForVisitorHandler>();
        // `14-13`/`adr/0079` decision 5: the preferred-reply-channel write path - see the handler's own
        // remarks for why it is gated on ConversationSend rather than a channel-management permission.
        services.AddScoped<SetPreferredChannelIdentityHandler>();
        // `20-11`: the per-booking priority list - the deferred second half of `20-09`'s own primary-
        // phone gate. Same permission/registration shape as SetPreferredChannelIdentityHandler right
        // above, one level narrower (per booking, not per visitor).
        services.AddScoped<SetModuleTaskChannelPriorityListHandler>();
        services.AddScoped<ListModuleTaskChannelPriorityListHandler>();

        // `14-02`/`adr/0069`: bound here, with WebhookSecretCipherOptions right above it - the same
        // fail-fast-on-a-missing-key discipline, a different named section and a different key.
        services
            .AddOptions<ChannelCredentialCipherOptions>()
            .Bind(configuration.GetSection(ChannelCredentialCipherOptions.SectionName))
            .Validate(IsValidBase64Aes256Key, "Channels:CredentialEncryptionKey must be a base64-encoded 32-byte AES-256 key.")
            .ValidateOnStart();
        // Found live, 2026-08-28: missing at 14-02's own merge - ChannelCredentialCipher's
        // constructor takes the raw ChannelCredentialCipherOptions, not IOptions<T> (the same shape
        // WebhookSecretCipher above it uses), so without this line the DI container has bound and
        // validated the options but never made the type itself resolvable - a "startup failure that
        // waits for the first request" rather than the fail-fast-on-boot every other option on this
        // page gets, because nothing calls RegisterChannelCredentialHandler until an operator actually
        // tries to connect a channel. No integration test caught it because the handler tests resolve
        // FakeChannelCredentialCipher directly, never through this real registration path.
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChannelCredentialCipherOptions>>().Value);
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

        // `14-07`: Telegram's own outbound client and adapter - the same "registered everywhere,
        // resolved where it matters" shape as MAX above.
        services
            .AddOptions<TelegramBotApiOptions>()
            .Bind(configuration.GetSection(TelegramBotApiOptions.SectionName))
            .ValidateOnStart();
        services
            .AddOptions<TelegramLongPollingServiceOptions>()
            .Bind(configuration.GetSection(TelegramLongPollingServiceOptions.SectionName))
            .ValidateOnStart();
        // `14-07`/`adr/0070`: this deployment's own outbound SOCKS5 relay - see TelegramProxyOptions'
        // own remarks for why it is deployment configuration, not a tenant secret, and why it is wired
        // here (the host's composition root) rather than inside TelegramApiClient itself.
        services
            .AddOptions<TelegramProxyOptions>()
            .Bind(configuration.GetSection(TelegramProxyOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient<TelegramApiClient>((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<TelegramBotApiOptions>>().Value.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var proxyAddress = sp.GetRequiredService<IOptions<TelegramProxyOptions>>().Value.Socks5Address;
            var handler = new SocketsHttpHandler();
            if (!string.IsNullOrWhiteSpace(proxyAddress))
            {
                // SocketsHttpHandler natively understands a socks5:// proxy URI (.NET 5+) - no
                // third-party SOCKS package needed, the same "no NuGet package without saying what it
                // replaces" discipline CLAUDE.md asks for.
                handler.Proxy = new WebProxy(new Uri($"socks5://{proxyAddress}"));
                handler.UseProxy = true;
            }

            return handler;
        })
        // Found live 2026-08-28: HttpClientFactory's own default logging handlers redact header
        // *values* but log the request URI in full - safe for MAX (auth in a header) and a real token
        // leak for Telegram (auth in the URL path, TelegramBotApiOptions' own remarks). RemoveAllLoggers
        // strips those defaults; TelegramTokenRedactingLoggingHandler (its own remarks have the full
        // story) replaces them with the same shape of log line, token redacted structurally rather than
        // simply omitted, so this client keeps the operational visibility MAX's own gets for free.
        .RemoveAllLoggers()
        .AddHttpMessageHandler<TelegramTokenRedactingLoggingHandler>();
        services.AddTransient<TelegramTokenRedactingLoggingHandler>();
        // Found 2026-09-02, auditing the same leak against the *other* telemetry signal: the handler
        // above closes it for logs and does nothing for traces. OpenTelemetry's HttpClient
        // instrumentation (wired by every host's AddPlatformObservability) listens to
        // System.Net.Http's DiagnosticSource from below the handler chain and records the outgoing URL
        // as `url.full`, token and all. This registers the one hook that can overwrite that tag - see
        // TelegramTraceUrlRedaction's own remarks for why enrichment rather than a filter, and why the
        // redaction is a product concern rather than something Ago.Platform.Observability should know.
        services.AddTelegramTokenTraceRedaction();
        // Singleton, not scoped - the identical reasoning MaxChannelAdapter's own remarks give: the
        // singleton InboundChannelAdapterRegistry can only ever hold adapters safe to keep for the
        // process lifetime.
        services.AddSingleton<TelegramChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<TelegramChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        // `14-08`: VK's own outbound client and adapter - the same "registered everywhere, resolved
        // where it matters" shape as MAX/Telegram above. No VkLongPollingServiceOptions to bind: unlike
        // either precedent, this channel has no polling loop at all (VkChannelAdapter's own remarks).
        services
            .AddOptions<VkBotApiOptions>()
            .Bind(configuration.GetSection(VkBotApiOptions.SectionName))
            .ValidateOnStart();
        // A *named* client, not AddHttpClient&lt;VkApiClient&gt;() - that typed-client shape needs a
        // constructor HttpClientFactory can build by resolving every parameter from DI, and
        // VkApiClient's second parameter (the configured API version, a plain string) is not itself a
        // DI service. Registering the client by name and constructing VkApiClient explicitly below is
        // the standard escape hatch for a typed client that needs more than just an HttpClient.
        services.AddHttpClient(nameof(VkApiClient), (sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<VkBotApiOptions>>().Value.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        });
        services.AddSingleton(sp =>
            new VkApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(VkApiClient)),
                sp.GetRequiredService<IOptions<VkBotApiOptions>>().Value.ApiVersion));
        // Singleton, not scoped - the identical reasoning MaxChannelAdapter's/TelegramChannelAdapter's
        // own remarks give: the singleton InboundChannelAdapterRegistry can only ever hold adapters safe
        // to keep for the process lifetime.
        services.AddSingleton<VkChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<VkChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        // `14-11`: Avito's own outbound client and adapter - the same "registered everywhere, resolved
        // where it matters" shape as MAX/Telegram/VK above. No AvitoLongPollingServiceOptions to bind -
        // unlike MAX/Telegram, this channel has no polling loop at all (AvitoChannelAdapter's own
        // remarks), the same "webhook only" shape VK already established.
        services
            .AddOptions<AvitoApiOptions>()
            .Bind(configuration.GetSection(AvitoApiOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient<AvitoApiClient>((sp, client) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<AvitoApiOptions>>().Value.BaseUrl;
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        });
        // Singleton, not scoped - the identical reasoning MaxChannelAdapter's/VkChannelAdapter's own
        // remarks give: the singleton InboundChannelAdapterRegistry can only ever hold adapters safe to
        // keep for the process lifetime. AvitoChannelAdapter takes IOptions<AvitoApiOptions> (not the
        // plain value) so IOptionsMonitor-backed reloads of Channels:Avito:ClientId/ClientSecret are
        // observed by the one caller (RefreshAndPersistAsync) that ever reads them, the same "no plain
        // value snapshot for a value a token-refresh call reads live" reasoning that already applies
        // to every other IOptions<T> registration on this page.
        services.AddSingleton<AvitoChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<AvitoChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        // `14-10`: WhatsApp's own outbound client and adapter - the same "registered everywhere, resolved
        // where it matters" shape as MAX/Telegram/VK above. AppSecret/VerifyToken are left unbound-and-
        // unvalidated-on-start deliberately (WhatsAppBotApiOptions' own remarks: a deployment that has not
        // configured Channels:WhatsApp at all must still start) - WhatsAppChannelEndpoints/
        // WhatsAppWebhookEndpoints each refuse at the point of use instead.
        services
            .AddOptions<WhatsAppBotApiOptions>()
            .Bind(configuration.GetSection(WhatsAppBotApiOptions.SectionName));
        services.AddHttpClient(nameof(WhatsAppApiClient), (sp, client) =>
        {
            var whatsAppOptions = sp.GetRequiredService<IOptions<WhatsAppBotApiOptions>>().Value;
            var baseUrl = $"{whatsAppOptions.BaseUrl.TrimEnd('/')}/{whatsAppOptions.ApiVersion}/";
            client.BaseAddress = new Uri(baseUrl);
        });
        services.AddSingleton(sp =>
            new WhatsAppApiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WhatsAppApiClient))));
        // Singleton, not scoped - the identical reasoning MaxChannelAdapter's/TelegramChannelAdapter's/
        // VkChannelAdapter's own remarks give: the singleton InboundChannelAdapterRegistry can only ever
        // hold adapters safe to keep for the process lifetime.
        services.AddSingleton<WhatsAppChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<WhatsAppChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        // `14-09`: Email's own SMTP client and adapter. Domain/WebhookSecret left unbound-and-unvalidated-
        // on-start deliberately, the identical "a deployment that has not configured this channel at all
        // must still start" shape WhatsAppBotApiOptions' own remarks state - EmailWebhookEndpoints refuses
        // at the point of use instead (no EmailChannelEndpoints to also guard: EmailBotApiOptions' own
        // remarks explain why this channel ships no console connect/disconnect endpoint at all).
        services
            .AddOptions<EmailBotApiOptions>()
            .Bind(configuration.GetSection(EmailBotApiOptions.SectionName));
        services.AddSingleton(sp => new EmailSmtpClient(sp.GetRequiredService<IOptions<EmailBotApiOptions>>().Value));
        // Singleton, not scoped - the identical reasoning every channel adapter above gives: the
        // singleton InboundChannelAdapterRegistry can only ever hold adapters safe to keep for the
        // process lifetime. EmailChannelAdapter takes IOptions<EmailBotApiOptions> (not the plain value),
        // matching AvitoChannelAdapter's own reasoning, even though nothing here reloads it live today -
        // the same "no plain value snapshot" convention every other IOptions<T> registration on this page
        // follows.
        services.AddSingleton<EmailChannelAdapter>();
        services.AddSingleton<IInboundChannelAdapter>(sp => new ResilientInboundChannelAdapter(
            sp.GetRequiredService<EmailChannelAdapter>(), sp.GetRequiredService<ChannelResiliencePipelines>()));

        // `20-07`/`adr/0065`: the module HTTP boundary - the same "registered everywhere, resolved
        // where it matters" shape as the channel adapters above. Unlike MAX/Telegram, HttpModuleGateway's
        // typed HttpClient carries no BaseAddress (its own remarks: a module's entry point is a
        // per-site, per-module value from the registry, never a fixed one this deployment configures).
        services.AddHttpClient<HttpModuleGateway>();
        services.AddResiliencePipelineOptions(
            ModuleResiliencePipelines.PipelineName, configuration, ConfigureModuleResilienceDefaults);
        services.AddSingleton(sp => new ModuleResiliencePipelines(
            sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>()
                .Get(ModuleResiliencePipelines.PipelineName)));
        services.AddScoped<IModuleGateway>(sp => new ResilientModuleGateway(
            sp.GetRequiredService<HttpModuleGateway>(), sp.GetRequiredService<ModuleResiliencePipelines>()));

        // `22-11`: the provisioning boundary - a sibling to the module task boundary just above, not a
        // third method on it (IModuleRegistrationGateway's own remarks). Deliberately unwrapped by any
        // resilience pipeline: a rare, operator-initiated call, not hot-path traffic shared across every
        // visitor message - HttpModuleRegistrationGateway's own remarks.
        services.AddHttpClient<HttpModuleRegistrationGateway>();
        services.AddScoped<IModuleRegistrationGateway, HttpModuleRegistrationGateway>();
        services.AddSingleton<IModuleCredentialGenerator, ModuleCredentialGenerator>();

        services.AddScoped<EnableModuleForSiteHandler>();
        services.AddScoped<RotateModuleCredentialHandler>();
        services.AddScoped<RevokeModuleForSiteHandler>();
        services.AddScoped<VerifyModuleRegistrationHandler>();
        // `23-01`: the console's own read of this route group - see the handler's own remarks for why
        // it exists at all (the endpoint used to call IEnabledModuleReadStore directly, ungated).
        services.AddScoped<ListEnabledModulesForSiteHandler>();
        // `22-17`: the platform owner's own grant/revoke - a second caller of the identical `22-11`
        // module-first registration mechanism above, gated entirely by RequirePlatformOwner at the
        // route rather than by IPermissionChecker (OwnerModuleEndpoints' own remarks).
        services.AddScoped<EnableModuleForSiteAsOwnerHandler>();
        services.AddScoped<RevokeModuleForSiteAsOwnerHandler>();
        // `20-07`: resolved once per MessageAccepted delivery by Ago.Chat.Worker's own ModuleTaskConsumer
        // - the identical shape SendOfflineAutoReplyHandler is registered and resolved with.
        services.AddScoped<RouteConversationToModuleHandler>();

        // `18-14`: which module key the chat-to-booking conversion report means - a config value, never
        // a literal (ModuleFlowReportOptions' own remarks on why). No `.Validate()` predicate checking
        // mere non-emptiness would catch a value with the wrong charset/length, so the predicate below
        // constructs a real Domain.ModuleKey from the bound value and rejects anything that throws -
        // the same "run the real construction, don't half-reimplement its rules" shape
        // IsValidBase64Aes256Key below already sets for the two cipher-key options.
        services
            .AddOptions<ModuleFlowReportOptions>()
            .Bind(configuration.GetSection(ModuleFlowReportOptions.SectionName))
            .Validate(IsValidModuleKey, "ModuleFlowReport:ModuleKey must be a valid, non-empty module key.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ModuleFlowReportOptions>>().Value);

        // `23-16`: the ranking threshold `ConversionReportReadStore`/`TagBreakdownReadStore` both take a
        // dependency on directly (AnalyticsOptions' own remarks on why one shared options class, and why
        // a non-negative int is the whole validation this needs - unlike ModuleFlowReportOptions' own
        // ModuleKey, there is no real construction to run against it).
        services
            .AddOptions<AnalyticsOptions>()
            .Bind(configuration.GetSection(AnalyticsOptions.SectionName))
            .Validate(o => o.MinimumSampleForRate >= 0, "Analytics:MinimumSampleForRate must not be negative.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AnalyticsOptions>>().Value);

        // `13-02`/`adr/0025`: bound here, with WebhookSecretCipherOptions/ChannelCredentialCipherOptions
        // above - PricePerSeatRub deliberately ships no code default (BillingOptions' own remarks:
        // "measure or stay silent" applies with more force to a figure that charges a real card), so
        // .ValidateOnStart() alone (no .Validate() predicate) is what turns "left at 0" into a startup
        // failure - a positive check is added explicitly below since the CLR default for `decimal` (0)
        // would otherwise satisfy a binder with nothing to complain about.
        services
            .AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName))
            .Validate(o => o.PricePerSeatRub > 0, "Billing:PricePerSeatRub must be set to a positive value.")
            .Validate(o => Uri.IsWellFormedUriString(o.CheckoutReturnUrl, UriKind.Absolute), "Billing:CheckoutReturnUrl must be an absolute URL.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<BillingOptions>>().Value);

        // `13-02`/`adr/0025`: our own fixed ЮKassa application credentials - see YooKassaOptions' own
        // remarks for the contrast with WebhookSecretCipherOptions' per-tenant ciphertext shape right
        // above (a different reason to change, a different validation - non-empty strings, not a
        // base64-32-byte key).
        services
            .AddOptions<YooKassaOptions>()
            .Bind(configuration.GetSection(YooKassaOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ShopId), "Billing:YooKassa:ShopId must be set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SecretKey), "Billing:YooKassa:SecretKey must be set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookKey), "Billing:YooKassa:WebhookKey must be set.")
            .ValidateOnStart();
        services.AddHttpClient<YooKassaPaymentsApiClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<YooKassaOptions>>().Value;
            var baseUrl = options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            // Basic auth, shop id as the username - ЮKassa's own documented scheme for the Payments
            // API. Set once here, at the composition root, rather than per-request inside
            // YooKassaPaymentsApiClient - the same "ChatModule builds the HttpClient, the client class
            // stays thin" split TelegramApiClient's own remarks describe for its base address.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ShopId}:{options.SecretKey}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        });
        // `13-03`: BillingResiliencePipeline wraps only ChargeStoredPaymentMethodAsync
        // (ResilientYooKassaPaymentsClient's own remarks on why CreatePaymentAsync stays unwrapped) -
        // the same "named options group, constructed through a factory that resolves it" shape
        // ChannelResiliencePipelines' own registration just above already establishes, for the
        // identical "ChatModule runs inside every host" reason.
        services.AddResiliencePipelineOptions(
            BillingResiliencePipeline.PipelineName, configuration, ConfigureBillingResilienceDefaults);
        services.AddSingleton(sp => new BillingResiliencePipeline(
            sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(BillingResiliencePipeline.PipelineName)));
        services.AddScoped<IYooKassaPaymentsClient>(sp => new ResilientYooKassaPaymentsClient(
            sp.GetRequiredService<YooKassaPaymentsApiClient>(), sp.GetRequiredService<BillingResiliencePipeline>()));
        services.AddSingleton<IYooKassaWebhookSignatureVerifier>(sp =>
            new YooKassaWebhookSignatureVerifier(sp.GetRequiredService<IOptions<YooKassaOptions>>().Value));
        services.AddScoped<CreateCheckoutSessionHandler>();
        services.AddScoped<ProcessYooKassaWebhookHandler>();
        // `13-03`: the recurring-charge job's own multi-aggregate transaction, and the two write paths
        // this item's own new billing endpoints need - see each type's own remarks.
        services.AddScoped<ISubscriptionRenewalApplier, SubscriptionRenewalApplier>();
        services.AddScoped<ProcessSubscriptionRenewalHandler>();
        services.AddScoped<ISeatChangeApplier, SeatChangeApplier>();
        services.AddScoped<CancelSubscriptionHandler>();
        services.AddScoped<ChangeSubscriptionSeatsHandler>();
        // `13-04`: the console billing screen's own bootstrap read - GetBillingStatus's own remarks.
        services.AddScoped<GetBillingStatusHandler>();

        // `19-01`: bound here, not a host's own Program.cs - GenerateReplyDraftHandler is registered
        // for every host below, the same "plain value, not IOptions<T>" shape MessageSendRateLimitOptions/
        // AttachmentRateLimitOptions above already establish.
        services
            .AddOptions<ReplyDraftOptions>()
            .Bind(configuration.GetSection(ReplyDraftOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReplyDraftOptions>>().Value);
        services
            .AddOptions<ReplyDraftRateLimitOptions>()
            .Bind(configuration.GetSection(ReplyDraftRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReplyDraftRateLimitOptions>>().Value);

        // `19-01`/`adr/0078`: our own fixed YandexGPT application credentials - see YandexGptOptions'
        // own remarks for the contrast with a per-tenant secret shape. No `.Validate()`/
        // `.ValidateOnStart()` here, deliberately - a missing ApiKey/FolderId used to fail every host's
        // own startup outright, which took the rest of that host down for a feature no environment yet
        // has real credentials for. Reply-draft assist is now an optional capability, decided below by
        // which IReplyDraftGenerator gets registered - the same "degrade one feature, not the process"
        // choice `UnconfiguredReplyDraftGenerator`'s own remarks describe in full.
        services
            .AddOptions<YandexGptOptions>()
            .Bind(configuration.GetSection(YandexGptOptions.SectionName));
        var yandexGptOptions = configuration.GetSection(YandexGptOptions.SectionName).Get<YandexGptOptions>() ?? new();
        if (!string.IsNullOrWhiteSpace(yandexGptOptions.ApiKey) && !string.IsNullOrWhiteSpace(yandexGptOptions.FolderId))
        {
            services.AddHttpClient<YandexGptReplyDraftClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<YandexGptOptions>>().Value;
                var baseUrl = opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                // Yandex Cloud's own documented static-key scheme, not Bearer - set once here, at the
                // composition root, the same "ChatModule builds the HttpClient, the client class stays
                // thin" split YooKassaPaymentsApiClient's own remarks describe for its Basic-auth header.
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Api-Key", opts.ApiKey);
            });
            // `19-01`: ReplyDraftResiliencePipeline wraps the whole call, unlike BillingResiliencePipeline
            // (which leaves CreatePaymentAsync unwrapped) - see ResilientReplyDraftGenerator's own remarks
            // on why an interactive, human-waiting HTTP request still gets a pipeline here, and on why its
            // own degrade-to-Unavailable happens inside the decorator rather than propagating.
            services.AddResiliencePipelineOptions(
                ReplyDraftResiliencePipeline.PipelineName, configuration, ConfigureReplyDraftResilienceDefaults);
            services.AddSingleton(sp => new ReplyDraftResiliencePipeline(
                sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(ReplyDraftResiliencePipeline.PipelineName)));
            services.AddScoped<IReplyDraftGenerator>(sp => new ResilientReplyDraftGenerator(
                sp.GetRequiredService<YandexGptReplyDraftClient>(),
                sp.GetRequiredService<ReplyDraftResiliencePipeline>(),
                sp.GetRequiredService<ILogger<ResilientReplyDraftGenerator>>()));
        }
        else
        {
            services.AddScoped<IReplyDraftGenerator, UnconfiguredReplyDraftGenerator>();
        }
        services.AddScoped<GenerateReplyDraftHandler>();

        // `19-02`: registered here too - CategorizeConversationHandler is reached only from
        // `Ago.Chat.Worker.ConversationCategorizationJob` (this feature has no operator-facing endpoint
        // at all), but ChatModule's own DI wiring runs identically for every host regardless of which
        // job or endpoint actually calls a given handler, the same "bound here, not a host's own
        // Program.cs" convention the ReplyDraft block just above already establishes.
        services
            .AddOptions<CategorizationOptions>()
            .Bind(configuration.GetSection(CategorizationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CategorizationOptions>>().Value);

        // `19-02`/`adr/0078`: our own fixed YandexGPT application credentials for this feature - its
        // own section, not a second binding of `YandexGptOptions` (`CategorizationYandexGptOptions`'s
        // own remarks explain why). No `.Validate()`/`.ValidateOnStart()` - the identical "optional
        // capability, decided by which port implementation gets registered" choice the ReplyDraft block
        // above makes, for the same reason (`UnconfiguredConversationCategorizer`'s own remarks).
        services
            .AddOptions<CategorizationYandexGptOptions>()
            .Bind(configuration.GetSection(CategorizationYandexGptOptions.SectionName));
        var categorizationYandexGptOptions =
            configuration.GetSection(CategorizationYandexGptOptions.SectionName).Get<CategorizationYandexGptOptions>() ?? new();
        if (!string.IsNullOrWhiteSpace(categorizationYandexGptOptions.ApiKey)
            && !string.IsNullOrWhiteSpace(categorizationYandexGptOptions.FolderId))
        {
            services.AddHttpClient<YandexGptConversationCategorizerClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<CategorizationYandexGptOptions>>().Value;
                var baseUrl = opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/";
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Api-Key", opts.ApiKey);
            });
            // `19-02`: CategorizationResiliencePipeline wraps the whole call - see
            // ResilientConversationCategorizer's own remarks on why an unattended batch job still gets a
            // pipeline (so one bad tick cannot hammer a down provider once per candidate conversation).
            services.AddResiliencePipelineOptions(
                CategorizationResiliencePipeline.PipelineName, configuration, ConfigureCategorizationResilienceDefaults);
            services.AddSingleton(sp => new CategorizationResiliencePipeline(
                sp.GetRequiredService<IOptionsMonitor<ResiliencePipelineOptions>>().Get(CategorizationResiliencePipeline.PipelineName)));
            services.AddScoped<IConversationCategorizer>(sp => new ResilientConversationCategorizer(
                sp.GetRequiredService<YandexGptConversationCategorizerClient>(),
                sp.GetRequiredService<CategorizationResiliencePipeline>(),
                sp.GetRequiredService<ILogger<ResilientConversationCategorizer>>()));
        }
        else
        {
            services.AddScoped<IConversationCategorizer, UnconfiguredConversationCategorizer>();
        }
        services.AddScoped<CategorizeConversationHandler>();

        services.AddScoped<StartConversationHandler>();
        services.AddScoped<SendVisitorMessageHandler>();
        services.AddScoped<SendOperatorMessageHandler>();
        services.AddScoped<GetConversationHistoryHandler>();
        services.AddScoped<GetSiteConfigByPublicKeyHandler>();
        services.AddScoped<GetSiteConfigByIdHandler>();
        services.AddScoped<CheckCorsOriginHandler>();
        services.AddScoped<AssignConversationHandler>();
        // `18-02`: the same assignment machinery used a second way - see the handler's own remarks.
        services.AddScoped<TransferConversationHandler>();
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
        // `18-01`
        services.AddScoped<SearchConversationsHandler>();
        // `18-08`: the console's own basic self-service report - see the handler's own remarks for
        // why it shares GetAllConversationsForSiteHandler/SearchConversationsHandler's permission gate.
        services.AddScoped<GetOperatorAnalyticsForSiteHandler>();
        // `18-10`: the outcome/conversion report - found missing here while landing `18-11` (its own
        // GetTagBreakdownReportForSiteHandler registration, right below, is what surfaced the gap by
        // comparison). The read-store (ConversionReportReadStore) was registered by `18-10` in
        // ServiceCollectionExtensions.cs; only this handler registration was missing, so the
        // `/conversion-report` endpoint's DI resolution failed on every request until now.
        services.AddScoped<GetConversionReportForSiteHandler>();
        // `18-14`: the console's own chat-to-booking conversion report - its own read-store and its
        // own permission gate call (GetModuleFlowReportForSiteHandler's own remarks), sharing
        // ModuleFlowReportOptions bound above rather than a second binding site.
        services.AddScoped<GetModuleFlowReportForSiteHandler>();
        // `18-11`: the tag breakdown report - its own read-store and its own permission gate call
        // (GetTagBreakdownReportForSiteHandler's own remarks), sharing GetOperatorAnalyticsForSiteHandler's
        // Permission.SiteConfigure gate rather than a new one.
        services.AddScoped<GetTagBreakdownReportForSiteHandler>();
        // `18-07`: the returning-visitor-history panel's own read - see the handler's own remarks.
        services.AddScoped<GetVisitorHistoryHandler>();
        services.AddScoped<DeleteAttachmentHandler>();
        services.AddScoped<GetMyPermissionsHandler>();
        // `6-02`: the first real caller of Conversation.Close() - see the handler's own remarks.
        services.AddScoped<CloseConversationHandler>();
        // `18-06`: the system-initiated twin of CloseConversationHandler, resolved once per
        // conversation from a fresh IServiceScopeFactory scope by AutoCloseInactiveConversationsJob
        // (Ago.Chat.Worker) - see the handler's own remarks for why it is a second handler rather than
        // a nullable OperatorId branch on the one above.
        services.AddScoped<AutoCloseConversationHandler>();
        // `5-15`: the unread counter's first-ever downward writer - see the handler's own remarks.
        services.AddScoped<MarkConversationReadHandler>();
        // `10-02`
        services.AddScoped<RegisterSiteHandler>();
        // `13-07`/`adr/0068`: the console switcher's own read - see the handler's own remarks.
        services.AddScoped<ListMyTenanciesHandler>();
        // `13-01`: `Permission.SiteManageOperators`'s first real write-path caller, and the seat-limit
        // entitlement check's one enforcement point - see each handler's own remarks.
        services.AddScoped<CreateOperatorInviteHandler>();
        services.AddScoped<RedeemOperatorInviteHandler>();
        // `13-03`: the seat-assignment and operator-removal mechanism `13-01` named but did not build -
        // see each handler's own remarks.
        services.AddScoped<ToggleOperatorSeatHandler>();
        services.AddScoped<RemoveOperatorHandler>();
        services.AddScoped<GetSeatAssignmentSummaryHandler>();
        // `23-22`: the team screen's own bootstrap read - see the handler's own remarks.
        services.AddScoped<GetOperatorTeamHandler>();
        // `12-02`: only Ago.Chat.Api ever resolves this one (it backs a single HTTP endpoint gated by
        // `12-01`'s owner policy), registered here for the same reason as everything else on this
        // page - ChatModule is where handler registration lives, and a host that never maps the route
        // simply never resolves it.
        services.AddScoped<ListSitesForOwnerHandler>();
        // `23-14`: the per-tenant companion to the read above - same host, same policy, same
        // registration shape.
        services.AddScoped<GetSiteForOwnerHandler>();

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

        // `10-06`: the read `GetWidgetConfigHandler`'s own doc comment names as the gap this item
        // closes - a site's own public key and allowed origins, returned to that site's operators so
        // the console can render an installable snippet. Same registration shape, same reason, as the
        // pair right above.
        //
        // `23-06`: the "recently" threshold this handler now also needs - see SiteInstallationOptions'
        // own remarks for why it is configuration rather than a literal `7`.
        services
            .AddOptions<SiteInstallationOptions>()
            .Bind(configuration.GetSection(SiteInstallationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SiteInstallationOptions>>().Value);
        services.AddScoped<GetSiteInstallationHandler>();

        // `16-02`: the erase-request writes, and the completion-poll read the console needs since no
        // single-conversation admin-fetch endpoint existed before this item - see each handler's own
        // remarks. Registered for every host, the same shape as everything else on this page, even
        // though only Ago.Chat.Api maps HTTP endpoints for them today.
        services.AddScoped<RequestSiteErasureHandler>();
        services.AddScoped<RequestConversationErasureHandler>();
        services.AddScoped<GetConversationByIdHandler>();
        // `18-10`: found missing here the same way `GetConversionReportForSiteHandler` was - these two
        // outcome handlers (SetConversationOutcomeEndpoints' own actual read/write actions, not the
        // site-wide report) were never registered either, so `/outcome` (GET and POST) both crash-
        // looped the host at startup for the identical "handler | UNKNOWN" reason.
        services.AddScoped<SetConversationOutcomeHandler>();
        services.AddScoped<GetConversationOutcomeHandler>();

        // `16-03`: the export-request write and the completion-poll read, the same "registered for
        // every host, only Ago.Chat.Api maps routes for them today" shape as the erasure pair right
        // above.
        services.AddScoped<RequestSiteExportHandler>();
        services.AddScoped<GetSiteExportStatusHandler>();

        // `13-06`: the retrieval half of tenant retention archives - list what is available, then mint
        // a download URL for one period. No request/write handler alongside these two (unlike the
        // export pair above): the archive already exists by the time an operator could ask for it
        // (ListMessageArchivesHandler's own remarks), so there is nothing to enqueue.
        services.AddScoped<ListMessageArchivesHandler>();
        services.AddScoped<GetMessageArchiveDownloadUrlHandler>();

        // `14-04`: the offline auto-reply's three handlers. The read/write pair backs `Ago.Chat.Api`'s
        // own settings endpoints (the same `site:configure` gate `11-01`'s pair uses); the third is
        // resolved per message by `Ago.Chat.Worker`'s OfflineAutoReplyConsumer. Registered here for
        // every host, the same shape as everything else on this page - a host that maps no route and
        // runs no consumer simply never resolves them.
        services.AddScoped<GetOfflineAutoReplyHandler>();
        services.AddScoped<UpdateOfflineAutoReplyHandler>();
        services.AddScoped<SendOfflineAutoReplyHandler>();

        // `18-03`: the canned-response read/write pair backing `Ago.Chat.Api`'s own settings endpoints
        // and the console composer's picker - same `site:configure` gate, same "registered for every
        // host, only Ago.Chat.Api maps routes for them today" shape as the pair just above. No third
        // handler here - unlike `14-04`'s auto-reply, nothing on the message pipeline ever reads this
        // (Site.UpdateCannedResponses's own remarks on why there is no consumer to wire up).
        services.AddScoped<GetCannedResponsesHandler>();
        services.AddScoped<UpdateCannedResponsesHandler>();

        // `18-04`: notes and tags. AddConversationNoteHandler/GetConversationNotesHandler are the
        // only two handlers in this codebase that ever resolve INoteRepository - see that interface's
        // own remarks on why that narrowness is the leak-proofing itself, not an implementation
        // detail. The tag trio (Create/Rename/Delete) backs the management surface
        // (`site:configure`-gated, the same shape as the canned-response pair above); ListTags/
        // GetConversationTags/TagConversation/UntagConversation back the console's queue filter and
        // per-conversation tag picker.
        services.AddScoped<AddConversationNoteHandler>();
        services.AddScoped<GetConversationNotesHandler>();
        services.AddScoped<CreateTagHandler>();
        services.AddScoped<RenameTagHandler>();
        services.AddScoped<DeleteTagHandler>();
        services.AddScoped<ListTagsHandler>();
        services.AddScoped<GetConversationTagsHandler>();
        services.AddScoped<TagConversationHandler>();
        services.AddScoped<UntagConversationHandler>();

        // `14-14`/`adr/0079` section 6: unverified contact details - a phone/email/other fact an
        // operator recorded because a visitor said it, never because AGO Chat verified it. The three
        // handlers below are the only callers of IVisitorContactDetailRepository in this codebase.
        services.AddScoped<RecordVisitorContactDetailHandler>();
        services.AddScoped<ListVisitorContactDetailsHandler>();
        services.AddScoped<DeleteVisitorContactDetailHandler>();

        // `14-15`/`adr/0079`: phone verification via a proactive SMS/voice code - see this item's own
        // backlog file, "Why this cannot reuse 14-12's mechanism". Both handlers share one options
        // group (`PhoneVerificationOptions`), the same `PendingChannelLinkRequestOptions` shape.
        services
            .AddOptions<PhoneVerificationOptions>()
            .Bind(configuration.GetSection(PhoneVerificationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PhoneVerificationOptions>>().Value);
        services
            .AddOptions<PhoneVerificationRateLimitOptions>()
            .Bind(configuration.GetSection(PhoneVerificationRateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PhoneVerificationRateLimitOptions>>().Value);
        services.AddScoped<InitiatePhoneVerificationHandler>();
        services.AddScoped<ConfirmPhoneVerificationHandler>();

        // The resilience shape a real gateway client would be wrapped in - built and unit-tested
        // (`PhoneVerificationResiliencePipeline`/`ResilientPhoneVerificationSender`'s own remarks), but
        // not wired into this registration: no gateway account exists in this environment
        // (`UnconfiguredPhoneVerificationSender`'s own remarks on why this registration, unlike
        // ReplyDraft's own `if (configured)` branch, is unconditional).
        services.AddScoped<IPhoneVerificationSender, UnconfiguredPhoneVerificationSender>();

        // 4-04: needed by both hosts - Ago.Chat.Api's OperatorHub (the query-at-disconnect fast
        // path) and Ago.Chat.Worker's OperatorDisconnectSweepJob (the periodic backstop).
        services.AddSingleton<OperatorPresencePublisher>();

        // `15-04`'s own IMessageArchiveGate registration lived here as AlwaysConfirmedMessageArchiveGate
        // (an Application-layer stand-in, no I/O) until `13-06` existed. The real, object-storage-backed
        // MessageArchiveGate now lives in Ago.Chat.Infrastructure.Postgres and is registered by
        // AddPostgresPersistence alongside every other Postgres-backed repository this Module composes -
        // see that project's ServiceCollectionExtensions and MessageArchiveGate's own remarks.
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

    /// <summary>
    /// `20-07`: starting points, not measured numbers - the identical caveat every resilience default
    /// on this page carries. Close to <see cref="ConfigureChannelResilienceDefaults"/>'s own numbers for
    /// the same reason those are close to the webhook dispatcher's: a module call is a request/response
    /// HTTP boundary a human is waiting on at the other end (the visitor mid-conversation), the same
    /// shape as a channel send, not a background job's own recurring charge. The backlog item's own
    /// Done-when also asks for an ADR recording the transport decision together with a measured step
    /// latency - not written as part of this change (out of this change's own explicit scope; see this
    /// repository's own report for the honest status) - whenever it is, these thresholds are what that
    /// measurement should be taken against, not a replacement for it.
    /// </summary>
    private static void ConfigureModuleResilienceDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(5) };
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 2,
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

    /// <summary>
    /// `13-03`: starting points, not measured numbers - the same caveat
    /// <see cref="ConfigureChannelResilienceDefaults"/>'s own remarks carry, restated here rather than
    /// simply reused because this boundary is a lower-frequency one (one background job's own recurring
    /// charge, not every operator reply on every channel) and a longer timeout costs nothing a human is
    /// waiting on - the bulkhead's own concurrency deliberately smaller too, since one Worker process
    /// running one renewal job at a time has no comparable concurrency to bound.
    /// </summary>
    private static void ConfigureBillingResilienceDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(10) };
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
        };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromMinutes(1),
            BreakDuration = TimeSpan.FromSeconds(30),
        };
        options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 2, MaxQueuedActions = 8 };
    }

    /// <summary>
    /// `19-01`: starting points, not measured numbers - the same caveat every other resilience default
    /// on this page carries. A shorter timeout and fewer retries than `ConfigureBillingResilienceDefaults`
    /// (an operator is watching a "Suggest a reply" button, unlike a background renewal job) but closer
    /// to `ConfigureChannelResilienceDefaults`'s own shape - a real human waiting on one HTTP response,
    /// same as a channel send. The bulkhead stays small: a reply draft is a low-frequency, one-at-a-time
    /// interaction per operator, not a fan-out send.
    /// </summary>
    private static void ConfigureReplyDraftResilienceDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(8) };
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(300),
        };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
        };
        options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 4, MaxQueuedActions = 16 };
    }

    /// <summary>
    /// `19-02`: starting points, not measured numbers - the same caveat every other resilience default
    /// on this page carries. Closer to `ConfigureBillingResilienceDefaults`'s own shape than to
    /// `ConfigureReplyDraftResilienceDefaults`'s: this call runs from an unattended
    /// `Ago.Chat.Worker.ConversationCategorizationJob` tick, not behind an operator's own button, so a
    /// longer timeout and more retry budget cost nothing an operator would notice - the same
    /// "recurring background job, not a human waiting" reasoning `BillingResiliencePipeline`'s own
    /// remarks give for `SubscriptionRenewalJob`. The circuit breaker still matters more here than for
    /// billing: one tick can call the provider once per candidate conversation in its own batch, so a
    /// down provider without a breaker would mean `ConversationCategorizationJobOptions.BatchSize`
    /// consecutive timeouts every cycle rather than a handful before the breaker opens.
    /// </summary>
    private static void ConfigureCategorizationResilienceDefaults(ResiliencePipelineOptions options)
    {
        options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(10) };
        options.Retry = new ResilienceRetryOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
        };
        options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromMinutes(1),
            BreakDuration = TimeSpan.FromSeconds(30),
        };
        options.Bulkhead = new ResilienceBulkheadOptions { MaxConcurrency = 2, MaxQueuedActions = 8 };
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

    // `18-14`: ModuleFlowReportOptions' own predicate - constructs a real Domain.ModuleKey from the
    // bound config value and rejects anything that throws, the same "run the real construction rather
    // than half-reimplement its rules" shape the two base64-key checks above already set.
    private static bool IsValidModuleKey(ModuleFlowReportOptions options)
    {
        try
        {
            _ = new ModuleKey(options.ModuleKey);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
