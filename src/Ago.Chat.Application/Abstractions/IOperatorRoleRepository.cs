using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-72`: the one write this codebase has ever needed against `operator_roles` outside bootstrap
/// registration (<see cref="ISiteRegistrationRepository"/>) and invite redemption
/// (<see cref="IOperatorInviteRedemptionRepository"/>) - replacing an existing colleague's whole role
/// assignment with exactly the one role a change names, the same single-role-per-invite shape
/// redemption already establishes for a freshly created operator. Its own port, not a fourth
/// <see cref="IRoleRepository"/> method: that port answers questions about a site's role catalogue, this
/// is a write against one specific operator's own assignment - a different resource, the same split
/// <see cref="IOperatorTeamReadStore"/>'s own remarks draw against <see cref="IOperatorRepository"/> for
/// an analogous "different question over the same table" case.
/// </summary>
public interface IOperatorRoleRepository
{
    /// <summary>
    /// Every role name <paramref name="operatorId"/> currently holds - `ChangeOperatorRoleHandler`'s own
    /// read for <c>RoleChangeRecordToWrite.PreviousRoleNames</c>, taken before the replacement below so
    /// the audit record states what was actually true a moment before the change, not the post-change
    /// state relabelled. Plural because an operator can hold more than one role at once (the account's
    /// own founder holds both seeded roles from registration).
    /// </summary>
    Task<IReadOnlyList<string>> GetRoleNamesAsync(OperatorId operatorId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every role <paramref name="operatorId"/> currently holds and assigns exactly
    /// <paramref name="roleId"/> instead. Must run inside the caller's own ambient transaction
    /// (<see cref="IUnitOfWork.BeginTransactionAsync"/>) opened on the same scoped connection - the same
    /// contract <see cref="IPermissionChecker.CountNonRemovedHoldersAsync"/> already states, since the
    /// whole point is committing this replacement atomically with the last-administrator check that
    /// decided whether to allow it (`ChangeOperatorRoleHandler`'s own remarks).
    /// </summary>
    Task ReplaceRoleAsync(OperatorId operatorId, Guid roleId, CancellationToken cancellationToken);
}
