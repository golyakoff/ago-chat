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

    /// <summary>
    /// `5-08`: every permission value string the operator's roles grant for this site, in one round
    /// trip - the console has no other way to learn "what can I do" to gate its own UI (show the
    /// admin nav item, show the attachment-delete button), and asking <see cref="HasPermissionAsync"/>
    /// once per permission the UI might ever care about does not scale the way a single resolved set
    /// does. Same resolution as <see cref="HasPermissionAsync"/>, just returning the set instead of
    /// testing membership in it - not a new mechanism, the same query with a different projection.
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionsAsync(
        OperatorId operatorId, SiteId siteId, CancellationToken cancellationToken);
}
