using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.LiftVisitorRestriction;

/// <summary>
/// `23-69`/`23-77`: the one reversal path both items' own Scope sections require - "it can be undone
/// before the expiry, and the undo is recorded too" (`23-69`), "it is reversible and recorded... exactly
/// as `24-10` already does" (`23-77`). Serves both kinds of restriction through the one port
/// (<see cref="IVisitorRestrictionRepository.LiftAsync"/>); which permission a caller needs depends on
/// *which kind* is actually active, read first via <see cref="IVisitorRestrictionRepository.GetActiveKindAsync"/>.
///
/// <para><b>Per-kind permission, not one shared gate.</b> <see cref="VisitorRestrictionKind.Spam"/>
/// needs <see cref="Permission.ConversationMarkSpam"/> - the same capability that can create one can
/// end it early, the identical "one permission for both directions" reasoning `24-10`'s own
/// <see cref="Permission.ConversationBlock"/> remarks state in full, restated per restriction kind
/// rather than assumed shared across both. <see cref="VisitorRestrictionKind.Block"/> needs
/// <see cref="Permission.ConversationBlock"/> for the identical reason - an ordinary Operator trusted
/// to mark spam is not thereby trusted to undo an Admin's own deliberate, indefinite block; an Admin
/// who also wants to lift a colleague's spam-mute needs to also hold <see cref="Permission.ConversationMarkSpam"/>
/// (ordinarily true in practice, since this codebase lets one operator hold multiple roles, but not
/// assumed here - each kind's own gate is checked on its own terms, the same non-hierarchical RBAC
/// model every other permission check in this codebase already uses).</para>
///
/// <para>The active kind is read, then the matching permission checked, then <c>LiftAsync</c> is
/// called - a second read-then-write with a narrow race (the restriction could naturally expire, or a
/// second lift land, between the read and the write) that is harmless either way: <c>LiftAsync</c>'s
/// own <see langword="false"/> return means "nothing was active to lift", turned into
/// <see cref="ConversationErrors.VisitorNotRestricted"/> below regardless of which of those two
/// explanations is the true one - the caller does not need to tell them apart, and re-deriving which
/// would need a second round trip for no operator-visible benefit.</para>
/// </summary>
public sealed class LiftVisitorRestrictionHandler(
    IVisitorRestrictionRepository restrictions, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(LiftVisitorRestriction command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var activeKind = await restrictions.GetActiveKindAsync(command.SiteId, command.VisitorId, now, cancellationToken);
        if (activeKind is null)
        {
            return ConversationErrors.VisitorNotRestricted(command.VisitorId.Value);
        }

        var requiredPermission = activeKind == VisitorRestrictionKind.Block
            ? Permission.ConversationBlock
            : Permission.ConversationMarkSpam;
        var allowed = await permissions.HasPermissionAsync(command.OperatorId, command.SiteId, requiredPermission, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to lift this visitor's restriction.");
        }

        var lifted = await restrictions.LiftAsync(command.SiteId, command.VisitorId, command.OperatorId, now, cancellationToken);
        if (!lifted)
        {
            // Lost the narrow race this handler's own remarks describe - the restriction expired or
            // was lifted by someone else between the read above and this write. Reported the same as
            // "was never restricted"; the caller does not need to tell the two apart.
            return ConversationErrors.VisitorNotRestricted(command.VisitorId.Value);
        }

        return Result.Success();
    }
}
