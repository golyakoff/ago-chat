using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RestoreOperatorSeatAsOwner;

/// <summary>
/// `23-68`: the platform owner's own seat restore - see <see cref="RestoreOperatorSeatAsOwner"/>'s own
/// remarks for the full argument on why this is a deliberately separate command/handler from
/// <see cref="ToggleOperatorSeat.ToggleOperatorSeatHandler"/> rather than a nullable-<see cref="OperatorId"/>
/// branch on it: the fact that authorises this call - the <c>RequirePlatformOwner</c> policy on the
/// route that resolves this handler - does not live in a table <see cref="IPermissionChecker"/> could
/// check, so a permission check here would be a second, weaker copy of a decision the policy already
/// made (<see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>'s own remarks state
/// the identical argument in full). <see cref="IPermissionChecker"/> is never called.
///
/// <para><b>Restoring an already-held seat is a harmless no-op, not an error</b> - the same idempotent
/// posture <see cref="Domain.Operator.GoOnline"/>'s own remarks state for itself ("calling this while
/// already Online is a harmless no-op"). A runbook operator working from an incident report should not
/// have to first prove the seat is actually released before calling this.</para>
///
/// <para><b>A removed operator is refused, not silently no-op'd.</b> <see cref="Operator.RemovedAt"/>
/// is permanent - there is no "un-remove" in this codebase (<see cref="Operator.Remove"/>'s own
/// remarks) - so restoring a seat on a removed row would set a flag nothing downstream ever reads for
/// that row again and report success for an action that changed nothing real. Refusing names the actual
/// state plainly, matching this codebase's own "a refusal a person can act on" brief.</para>
///
/// <para><b>The role case, named rather than silently only-half-handled.</b> `23-68`'s own "Where this
/// is likely to go wrong" is explicit: the real incident this item was filed from left the target
/// operator's role assignment intact and only released their seat, so restoring the seat was the whole
/// fix - <see cref="Domain.Operator.CanSignIn"/> is <c>HoldsSeat || holdsManageOperatorsPermission</c>,
/// and setting <see cref="Operator.HoldsSeat"/> to <see langword="true"/> makes that expression
/// <see langword="true"/> unconditionally, regardless of any role. An operator who stripped their own
/// *last role* (not seat) is a different failure this handler does not reach: they can sign in again
/// once restored here, but hold no permission to do anything once inside, and nothing in this item
/// grants one back - `docs/runbooks/module-grant-and-revoke.md`'s sibling runbook entry for this item
/// says so explicitly, and the console surface built alongside this handler shows each operator's own
/// role names so the platform owner can see the gap rather than assume this call closed it.</para>
/// </summary>
public sealed class RestoreOperatorSeatAsOwnerHandler(
    IOperatorRepository operators, ISiteRepository sites, IOperatorSeatRestoreOverrideRepository overrides,
    IClock clock, IIdGenerator idGenerator)
{
    /// <summary>The identical "not a measured number, only a mistake-catcher" bound
    /// <see cref="RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength"/> uses,
    /// reusing the same precedent length rather than inventing a second one for the same shape of
    /// thing (a person typing a free-text justification).</summary>
    public const int MaxReasonLength = RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength;

    public async Task<Result<RestoreOperatorSeatOutcome>> HandleAsync(
        RestoreOperatorSeatAsOwner command, CancellationToken cancellationToken)
    {
        // `23-13`'s own "decide, don't default", restated for this override: checked first and
        // unconditionally on Force, before this handler loads the operator or site it would act on -
        // the identical ordering RevokeModuleForSiteAsOwnerHandler's own remarks explain for its
        // sibling override.
        if (command.Force)
        {
            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return ConversationErrors.OperatorSeatRestoreReasonRequired(
                    "A reason is required whenever force is set - state why this override is being used.");
            }

            if (command.Reason.Trim().Length > MaxReasonLength)
            {
                return ConversationErrors.OperatorSeatRestoreReasonRequired(
                    $"A seat-restore reason cannot exceed {MaxReasonLength} characters.");
            }
        }

        var target = await operators.GetByIdAsync(command.TargetOperatorId, command.SiteId, cancellationToken);
        if (target is null)
        {
            return ConversationErrors.OperatorNotFound(command.TargetOperatorId.Value);
        }

        if (target.RemovedAt is not null)
        {
            return ConversationErrors.OperatorAlreadyRemoved(command.TargetOperatorId.Value);
        }

        if (target.HoldsSeat)
        {
            // Already restored - nothing to change, nothing to override. See this class's own remarks
            // on why this is success, not an error.
            return new RestoreOperatorSeatOutcome(AlreadyHeldSeat: true, OverrodeSeatLimit: false);
        }

        var held = await operators.CountHeldSeatsAsync(command.SiteId, cancellationToken);
        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            throw new InvalidOperationException(
                $"Site {command.SiteId.Value} was not found while restoring an operator's seat - a foreign key "
                + "should have prevented this.");
        }

        var exceedsLimit = held + 1 > site.SeatLimit;
        if (exceedsLimit && !command.Force)
        {
            return ConversationErrors.OperatorSeatRestoreExceedsLimitRequiresForce(site.SeatLimit);
        }

        target.ToggleSeat(true);
        await operators.SaveAsync(target, cancellationToken);

        var overrodeSeatLimit = exceedsLimit && command.Force;
        if (overrodeSeatLimit)
        {
            var now = clock.UtcNow;
            await overrides.RecordAsync(
                idGenerator.NewId(now), command.SiteId, command.TargetOperatorId, command.RestoredBy,
                command.Reason!.Trim(), now, cancellationToken);
        }

        return new RestoreOperatorSeatOutcome(AlreadyHeldSeat: false, OverrodeSeatLimit: overrodeSeatLimit);
    }
}

/// <summary>What actually happened, for the console to report accurately rather than a bare "ok" -
/// <see cref="AlreadyHeldSeat"/> lets it say "nothing to do" instead of implying a change that did not
/// occur, and <see cref="OverrodeSeatLimit"/> lets it say the seat limit was knowingly exceeded rather
/// than staying silent about a fact the platform owner just caused.</summary>
public sealed record RestoreOperatorSeatOutcome(bool AlreadyHeldSeat, bool OverrodeSeatLimit);
