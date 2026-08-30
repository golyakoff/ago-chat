using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UnlinkChannelIdentityAsOwner;

/// <summary>
/// `14-12`/`adr/0079`: the platform owner's own unconditional unlink - `authorization.md`, as of this
/// item, names exactly one existing write/action surface for this actor to follow as precedent: none.
/// (`GET /api/v1/owner/sites`, `ListSitesForOwnerHandler`, was read-only.) This is the first.
///
/// <para><b>A wholly separate command and handler from <see cref="UseCases.UnlinkChannelIdentity.UnlinkChannelIdentityHandler"/>,
/// not a nullable-<see cref="Domain.OperatorId"/> parameter on it - the confirmed mechanism the backlog
/// item asked for, not assumed.</b> <see cref="ListSitesForOwnerHandler"/>'s own remarks state the reason
/// this codebase already settled on for the read case, and it applies with more force to a write: "the
/// fact that authorizes this call does not live in tables this codebase owns... inventing a port to
/// re-check at this layer what the policy already decided would be a second, weaker copy of the same
/// rule, drifting from the first the moment either changes." This handler therefore calls
/// <see cref="IPermissionChecker"/> for nothing at all - the sole gate is the `RequirePlatformOwner`
/// policy on the route that resolves it (`Ago.Chat.Api`'s <c>OwnerSitesEndpoints</c>-style mapping), the
/// same single-gate shape <see cref="ListSitesForOwnerHandler"/> already uses. A nullable-<see cref="Domain.OperatorId"/>
/// branch on the operator-gated handler instead would mean one caller's missing id silently skips the
/// <see cref="Domain.Permission.ChannelIdentityUnlink"/> check inside a handler whose only other caller
/// requires it - exactly the kind of "one flag flips off every check" shape this codebase avoids
/// elsewhere by keeping owner surfaces in their own class (<see cref="ListSitesForOwnerHandler"/> itself
/// never shared a class with <c>GetAllConversationsForSiteHandler</c> for the identical reason).</para>
///
/// <para><b>Still site-scoped in its own SQL, even though the caller is cross-tenant by design</b> - the
/// same distinction `authorization.md` draws for `GET /api/v1/owner/sites` itself ("cross-tenant on
/// purpose" is a property of what the *route* is allowed to reach, not license to skip validating that
/// the id in the URL and the id on the row actually agree). <paramref name="command"/>'s own
/// <see cref="Domain.SiteId"/> is checked against the loaded identity's real site so a caller cannot
/// unlink identity X by naming a different site's id in the path and having it silently succeed against
/// the wrong tenant's row.</para>
/// </summary>
public sealed class UnlinkChannelIdentityAsOwnerHandler(IChannelIdentityRepository identities, IClock clock)
{
    public async Task<Result> HandleAsync(UnlinkChannelIdentityAsOwner command, CancellationToken cancellationToken)
    {
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
