using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ToggleOperatorSeat;

/// <summary>
/// `13-03`: a single-aggregate write (<see cref="Operator.ToggleSeat"/>) - no outbox row, nothing else
/// in this codebase reacts to a seat toggle by itself (contrast <see cref="Operator.Remove"/>, which
/// raises <see cref="OperatorRemoved"/> because a Worker consumer genuinely needs to act on it).
///
/// <para><b>Toggling a seat on is capacity-checked against the site's current `seat_limit`; toggling
/// one off never is - an implementer's-call this item's own Scope left open ("up to the current
/// seat_limit"), decided here.</b> The over-seats condition (`13-03`'s own Scope) is real and is
/// deliberately never blocked when it arises from a downgrade (`decisions/0006`'s own rejection of
/// blocking a downgrade on live operator count) - but nothing in that decision says an owner should be
/// able to manufacture a *fresh* over-seats state by hand, one toggle at a time, when the site is not
/// already over its limit. Blocking a toggle-on that would push the held-seat count past `seat_limit`
/// mirrors `OperatorInviteRedemptionRepository`'s own seat check for the identical reason a new invite
/// is capacity-checked - the same `402 Payment Required` vocabulary
/// (<see cref="ConversationErrors.OperatorSeatLimitReached"/>), because "upgrade" is the real remedy,
/// not "retry".</para>
/// </summary>
public sealed class ToggleOperatorSeatHandler(
    IOperatorRepository operators, ISiteRepository sites, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(ToggleOperatorSeat command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage this site's operators.");
        }

        var target = await operators.GetByIdAsync(command.TargetOperatorId, command.SiteId, cancellationToken);
        if (target is null)
        {
            return ConversationErrors.OperatorNotFound(command.TargetOperatorId.Value);
        }

        if (command.HoldsSeat && !target.HoldsSeat)
        {
            var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
            if (site is null)
            {
                throw new InvalidOperationException(
                    $"Site {command.SiteId.Value} was not found while toggling an operator's seat - a foreign key should have prevented this.");
            }

            var held = await operators.CountHeldSeatsAsync(command.SiteId, cancellationToken);
            if (held >= site.SeatLimit)
            {
                return ConversationErrors.OperatorSeatLimitReached(site.SeatLimit);
            }
        }

        target.ToggleSeat(command.HoldsSeat);
        await operators.SaveAsync(target, cancellationToken);

        return Result.Success();
    }
}
