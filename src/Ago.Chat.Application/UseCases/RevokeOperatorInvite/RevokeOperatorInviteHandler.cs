using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeOperatorInvite;

/// <summary>
/// `25-73`: an ordinary load-mutate-save of the <see cref="OperatorInvite"/> aggregate through
/// <see cref="IOperatorInviteRepository"/> - unlike redemption, this never touches a second aggregate
/// (no `Site` row lock, no `Operator` row to create), so it needs none of
/// <see cref="Application.UseCases.RedeemOperatorInvite.RedeemOperatorInviteHandler"/>'s own wider
/// transaction, the same "its own port because it writes across more than one aggregate - never
/// otherwise" split that handler's own remarks draw.
///
/// <para><b>Redeemed/revoked are checked here, not only inside <see cref="OperatorInvite.Revoke"/>.</b>
/// The domain method already throws for both as a last line of defence (matching
/// <see cref="OperatorInvite.Redeem"/>'s own shape), but this handler checks first so an ordinary,
/// non-racing "revoke an already-redeemed invite" click gets a normal <c>Result</c>/<c>Error</c>
/// response - the same code this codebase already returns for the identical row-state conflict on the
/// redemption path - rather than an unhandled exception. A genuine race (two revoke clicks, or a revoke
/// racing a redemption) still throws through to a `500`; unlike redemption, this write has no
/// concurrent-caller story to build for (an admin does not race themselves clicking "отозвать" twice on
/// purpose), so no `xmin`-catch translation was added for it - flagged here rather than silently
/// assumed away.</para>
/// </summary>
public sealed class RevokeOperatorInviteHandler(
    IOperatorInviteRepository invites, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(RevokeOperatorInvite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage operators for this site.");
        }

        var invite = await invites.GetByIdAsync(command.OperatorInviteId, cancellationToken);
        // Wrong tenant reads like no such row - the same info-hiding shape `ErrorExtensions`' own
        // 404 group already applies to every other cross-tenant id lookup in this codebase.
        if (invite is null || invite.SiteId != command.SiteId)
        {
            return ConversationErrors.OperatorInviteNotFound();
        }

        if (invite.IsRedeemed)
        {
            return ConversationErrors.OperatorInviteAlreadyRedeemed();
        }

        if (invite.IsRevoked)
        {
            return ConversationErrors.OperatorInviteRevoked();
        }

        invite.Revoke(clock.UtcNow);
        await invites.SaveAsync(invite, cancellationToken);

        return Result.Success();
    }
}
