using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: the write-side (EF) port for <see cref="EnabledModule"/> - adr/0004's "EF for writes,
/// Dapper for reads" split, mirrored by <see cref="IEnabledModuleReadStore"/> on the read side.
/// </summary>
public interface IEnabledModuleRepository
{
    Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken);

    Task SaveAsync(EnabledModule module, CancellationToken cancellationToken);
}
