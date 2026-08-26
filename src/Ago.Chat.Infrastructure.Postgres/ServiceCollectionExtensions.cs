using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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

        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        services.AddScoped<IVisitorRepository, VisitorRepository>();
        // `14-01`
        services.AddScoped<IChannelIdentityRepository, ChannelIdentityRepository>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        // `10-02`
        services.AddScoped<ISiteRegistrationRepository, SiteRegistrationRepository>();
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
        // adr/0017: the one place a concrete DbContext type meets the generic platform writer.
        services.AddOutboxInbox<AgoChatDbContext>();

        return services;
    }
}
