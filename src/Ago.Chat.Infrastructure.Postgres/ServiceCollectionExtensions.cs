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
        // `12-02`: the cross-tenant operations read (IPlatformOverviewReadStore's own remarks on why
        // it is the only one and why it is safe).
        services.AddScoped<IPlatformOverviewReadStore, PlatformOverviewReadStore>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IOperatorCapacity, OperatorCapacityStore>();
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
        // `16-02`: the erase-request write - see IErasureRequestRepository's own remarks on why it is
        // its own port rather than a method on ISiteRepository/IConversationRepository.
        services.AddScoped<IErasureRequestRepository, ErasureRequestRepository>();
        // `16-03`: the export-request read/write - see IExportRequestRepository's own remarks.
        services.AddScoped<IExportRequestRepository, ExportRequestRepository>();
        // adr/0017: the one place a concrete DbContext type meets the generic platform writer.
        services.AddOutboxInbox<AgoChatDbContext>();

        return services;
    }
}
