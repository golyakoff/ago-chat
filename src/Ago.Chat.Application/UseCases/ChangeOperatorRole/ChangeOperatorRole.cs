using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ChangeOperatorRole;

/// <summary>
/// `23-72`: "an administrator can change an existing colleague's role, both directions" - the backlog
/// item's own Scope. <see cref="NewRoleName"/> is a plain string, the identical shape
/// <see cref="Application.UseCases.CreateOperatorInvite.CreateOperatorInvite"/> already uses for the same
/// reason: roles have no Domain/Application model of their own yet (<see cref="OperatorInvite"/>'s own
/// remarks), so a role is still named, not typed.
/// </summary>
public sealed record ChangeOperatorRole(OperatorId RequestedBy, SiteId SiteId, OperatorId TargetOperatorId, string NewRoleName);
