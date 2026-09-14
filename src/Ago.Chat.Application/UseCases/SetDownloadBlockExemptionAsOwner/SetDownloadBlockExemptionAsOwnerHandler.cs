using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner;

/// <summary>
/// `25-83`: sets or lifts the platform owner's own per-tenant bypass of the hard download-block
/// threshold - `Site.GrantDownloadBlockExemption`/`RevokeDownloadBlockExemption`'s own remarks for
/// what the flag does and does not change, and why this write raises no domain event.
///
/// <para><b>No <see cref="IPermissionChecker"/> call, for the identical reason every other owner
/// surface in this codebase gives</b> - the fact that authorizes this call (the
/// <c>RequirePlatformOwner</c> route policy) does not live in a table <see cref="IPermissionChecker"/>
/// could check, restated here from
/// <see cref="Ago.Chat.Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler"/>'s
/// own identical remarks.</para>
///
/// <para><b>The reason check runs first, unconditionally, before this handler ever touches
/// <see cref="ISiteRepository"/></b> - the same "reject the caller's input before touching another
/// system" ordering that handler's own class remarks already state, restated here for a second
/// owner-only flag.</para>
/// </summary>
public sealed class SetDownloadBlockExemptionAsOwnerHandler(ISiteRepository sites, IClock clock)
{
    /// <summary>Reused, not reinvented - the identical bound
    /// <c>RevokeModuleForSiteAsOwnerHandler.MaxReasonLength</c> already sets for the same shape of
    /// thing (a person typing a free-text justification for an owner-only override).</summary>
    public const int MaxReasonLength =
        Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner.RevokeModuleForSiteAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(SetDownloadBlockExemptionAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.DownloadBlockExemptionReasonRequired(
                "A reason is required whenever a tenant's download-block exemption is granted or revoked - state why.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.DownloadBlockExemptionReasonRequired(
                $"A download-block exemption reason cannot exceed {MaxReasonLength} characters.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;
        var reason = command.Reason.Trim();

        if (command.Exempt)
        {
            site.GrantDownloadBlockExemption(command.SetBy, reason, now);
        }
        else
        {
            site.RevokeDownloadBlockExemption(command.SetBy, reason, now);
        }

        await sites.SaveAsync(site, cancellationToken);

        return Result.Success();
    }
}
