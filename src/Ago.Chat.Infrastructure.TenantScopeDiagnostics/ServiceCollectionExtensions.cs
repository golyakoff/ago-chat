using Ago.Chat.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Infrastructure.TenantScopeDiagnostics;

/// <summary>
/// `24-17`. The same "only place that knows the concrete technology" shape
/// `Ago.Chat.Infrastructure.Postgres.ServiceCollectionExtensions.AddPostgresPersistence` already
/// establishes: `Ago.Chat.Api`'s `Program.cs` calls this by name and never sees Mono.Cecil.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantScopeDiagnostics(this IServiceCollection services)
    {
        // Stateless and side-effect-free beyond reading a file each call (TenantScopeInspector's own
        // remarks on why that read is never cached) - singleton costs nothing and avoids a
        // per-request allocation for a type with no per-request state to hold.
        services.AddSingleton<ITenantScopeInspector, TenantScopeInspector>();
        return services;
    }
}
