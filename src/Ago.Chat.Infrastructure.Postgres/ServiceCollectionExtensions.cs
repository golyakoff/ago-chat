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
        services.AddScoped<IVisitorRepository, VisitorRepository>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        services.AddScoped<IConversationReadStore, ConversationReadStore>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IOperatorCapacity, OperatorCapacityStore>();
        // adr/0017: the one place a concrete DbContext type meets the generic platform writer.
        services.AddOutboxInbox<AgoChatDbContext>();

        return services;
    }
}
