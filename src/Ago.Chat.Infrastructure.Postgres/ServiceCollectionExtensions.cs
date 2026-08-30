using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// The only place that knows this is Postgres (clean-architecture.md: "Hosts... AddPostgresPersistence()
/// extension methods live in their own Infrastructure projects and are selected by configuration").
/// `1-06` is its first real caller.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresPersistence(this IServiceCollection services, string connectionString)
    {
        // One NpgsqlDataSource for the process: EF's writes and Dapper's reads (ConversationReadStore)
        // share the same connection pool instead of each opening its own.
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);
        services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        // `8-08`: the read half of the schema check, registered for every host that persists anything
        // - which is every host. The *apply* half (SchemaMigrationApplier) is deliberately not
        // registered here: Ago.Chat.Migrator constructs it directly, so the type never enters a serving
        // host's container or its IL, which is what SchemaMigrationTests asserts (adr/0056: "applied by
        // its own deployable, and by nothing else").
        // SchemaGuardOptions itself is bound in ChatModule, where every other options group in this
        // product is bound - this method takes a connection string, not an IConfiguration, and
        // widening its signature to reach one would be the tail wagging the dog.
        services.AddScoped<SchemaVersionCheck>();

        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        services.AddScoped<IVisitorRepository, VisitorRepository>();
        // `14-01`
        services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        // `10-02`
        services.AddScoped<ISiteRegistrationRepository, SiteRegistrationRepository>();
        // `8-07`: the demo tenant lifecycle. Scoped like every other repository here; the credential
        // generator is a singleton because it holds no state and its only dependency is the platform's
        // own CSPRNG (the same shape WebhookSecretGenerator is registered with).
        services.AddScoped<IDemoTenantRepository, DemoTenantRepository>();
        services.AddSingleton<IDemoCredentialGenerator, DemoCredentialGenerator>();
        services.AddScoped<IConversationReadStore, ConversationReadStore>();
        // `18-01`: its own port - IConversationSearchStore's own remarks on why it is not a method on
        // IConversationReadStore.
        services.AddScoped<IConversationSearchStore, ConversationSearchStore>();
        // `12-02`: the cross-tenant operations read (IPlatformOverviewReadStore's own remarks on why
        // it is the only one and why it is safe).
        services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        // `18-08`: the console's own site-scoped "how am I doing" report.
        services.AddScoped<IOperatorAnalyticsReadStore, OperatorAnalyticsReadStore>();
        // `18-10`: the site owner's own conversion report, a sibling read store rather than a fourth
        // method on IOperatorAnalyticsReadStore - see that interface's own remarks for why.
        services.AddScoped<IConversionReportReadStore, ConversionReportReadStore>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IOperatorCapacity, OperatorCapacityStore>();
        // `18-02`: the transfer handler's own transaction boundary - see IUnitOfWork's own remarks
        // for why it exists at all.
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        // `6-03`
        services.AddScoped<IWebhookEndpointRepository, WebhookEndpointRepository>();
        services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
        services.AddScoped<IWebhookDeliveryReadStore, WebhookDeliveryReadStore>();
        services.AddSingleton<IWebhookSecretGenerator, WebhookSecretGenerator>();
        // Scoped, not singleton, so a missing/malformed Webhooks:SecretEncryptionKey surfaces on the
        // first request rather than only if something resolves it eagerly at startup - ChatModule's
        // own ValidateOnStart() on WebhookSecretCipherOptions is what actually fails fast for the
        // ordinary case; this is the same defense-in-depth belt-and-suspenders shape the constructor
        // guard in WebhookSecretCipher itself already applies.
        services.AddScoped<IWebhookSecretCipher, WebhookSecretCipher>();
        // `14-02`/`adr/0069`: same shape, same reasoning, a second cipher and a second key.
        services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        services.AddScoped<IChannelCredentialCipher, ChannelCredentialCipher>();
        // `13-01`
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IOperatorInviteRepository, OperatorInviteRepository>();
        services.AddScoped<IOperatorInviteRedemptionRepository, OperatorInviteRedemptionRepository>();
        services.AddSingleton<IOperatorInviteCodeGenerator, OperatorInviteCodeGenerator>();
        // `13-02`/`13-03`/`13-04`: the billing subscription's own read/write, and the webhook applier's
        // one-transaction multi-aggregate write - found missing here 2026-08-29 while landing `16-04`
        // (both types existed, and GetBillingStatusHandler/ProcessYooKassaWebhookHandler were already
        // registered depending on them, but nothing ever registered the ports themselves - a real,
        // unnoticed regression because no existing test resolves the full DI graph, only handlers
        // constructed directly against a fake).
        services.AddScoped<IBillingSubscriptionRepository, BillingSubscriptionRepository>();
        services.AddScoped<IBillingWebhookApplier, BillingWebhookApplier>();
        // `16-02`: the erase-request write - see IErasureRequestRepository's own remarks on why it is
        // its own port rather than a method on ISiteRepository/IConversationRepository.
        services.AddScoped<IErasureRequestRepository, ErasureRequestRepository>();
        // `16-03`: the export-request read/write - see IExportRequestRepository's own remarks.
        services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        // `13-06`: the archive manifest, and the real (object-storage-backed) gate that replaces
        // `15-04`'s AlwaysConfirmedMessageArchiveGate stand-in - see IMessageArchiveRepository's and
        // MessageArchiveGate's own remarks. Singleton, not Scoped like most repositories on this page:
        // both classes hold no state beyond an injected NpgsqlDataSource (itself Singleton, a
        // connection-pool factory rather than a live connection) and both are consumed directly by
        // Ago.Chat.Worker's singleton BackgroundServices (MessageArchiveJob, MessagePartitionPruneJob)
        // - a Scoped registration injected straight into a Singleton's constructor is the captive-
        // dependency bug MessageBatchWriter's own remarks on GetSiteConfigByIdHandler already found and
        // fixed once this same item; Singleton here avoids reintroducing it rather than working around
        // it with a per-cycle scope the way that fix needed to.
        services.AddSingleton<IMessageArchiveRepository, MessageArchiveRepository>();
        services.AddSingleton<IMessageArchiveGate, MessageArchiveGate>();
        // `18-04`: notes and tags - INoteRepository's own remarks on why it is registered here like
        // every other repository and yet reachable from only two handlers in the whole codebase.
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        // `20-07`: the registry's own EF write port and Dapper read store - adr/0004's split, the same
        // shape every other repository/read-store pair on this page follows.
        services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        // adr/0017: the one place a concrete DbContext type meets the generic platform writer.
        services.AddOutboxInbox<AgoChatDbContext>();

        return services;
    }
}
