using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// Resolves whether an operator holds a permission for a site (adr/0016). Visitors never go through
/// this port - a visitor holds a capability (its token proves it owns one conversation), not a role,
/// so a visitor's check is a direct participant comparison in the handler, not a lookup here.
/// </summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(
        OperatorId operatorId, SiteId siteId, Permission permission, CancellationToken cancellationToken);
}
