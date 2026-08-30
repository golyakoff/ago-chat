using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UnlinkChannelIdentity;

/// <summary>
/// `14-12`/`adr/0079` decision 4: flips <see cref="Domain.ChannelIdentity.Active"/> to
/// <see langword="false"/>, never a hard delete - <c>RevokeChannelCredentialHandler</c>'s own shape,
/// including the identical idempotent short-circuit for a retried/double-clicked request.
///
/// <para><b>Gated on <see cref="Domain.Permission.ChannelIdentityUnlink"/>, granted to no role by
/// default</b> (`adr/0016`'s granular-permission vocabulary, `Permission`'s own remarks on why this is
/// dedicated rather than folded into <see cref="Domain.Permission.ChannelManage"/>). The site owner's
/// own unconditional ability to unlink does not go through this handler at all - it reaches through the
/// separate, permission-free <c>UnlinkChannelIdentityAsOwnerHandler</c>, gated instead by the
/// `RequirePlatformOwner` policy at the API layer, the same "outside the RBAC model, not a hole in it"
/// shape `authorization.md` documents for every owner-only action and `ListSitesForOwnerHandler`'s own
/// remarks restate for its own single caller.</para>
/// </summary>
public sealed class UnlinkChannelIdentityHandler(
    IChannelIdentityRepository identities, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(UnlinkChannelIdentity command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Domain.Permission.ChannelIdentityUnlink, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to unlink channel identities for this site.");
        }

        var identity = await identities.GetByIdAsync(command.ChannelIdentityId, cancellationToken);
        if (identity is null || identity.SiteId != command.SiteId)
        {
            return ConversationErrors.ChannelIdentityNotFound(command.ChannelIdentityId.Value);
        }

        if (!identity.Active)
        {
            return Result.Success();
        }

        identity.Unlink(clock.UtcNow);
        await identities.SaveAsync(identity, cancellationToken);

        return Result.Success();
    }
}
