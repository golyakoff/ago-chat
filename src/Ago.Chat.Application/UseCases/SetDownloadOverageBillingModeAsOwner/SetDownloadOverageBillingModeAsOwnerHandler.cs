using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner;

/// <summary>
/// `25-84`: sets how one tenant pays for download overage - <see cref="Site.SetDownloadOverageBillingMode"/>
/// for what the value does.
///
/// <para><b>No <see cref="IPermissionChecker"/> call</b>, for the identical reason every other owner
/// surface in this codebase gives: the fact that authorizes this call (the <c>RequirePlatformOwner</c>
/// route policy) does not live in a table <see cref="IPermissionChecker"/> could check. Restated from
/// <see cref="Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner.SetDownloadBlockExemptionAsOwnerHandler"/>'s
/// own identical remarks rather than re-derived.</para>
///
/// <para><b>The reason check runs first, unconditionally</b> - the same "reject the caller's input
/// before touching another system" ordering that handler already uses, and the same reason bound, so
/// the two owner-only overrides on the same screen cannot disagree about how long a justification may
/// be.</para>
/// </summary>
public sealed class SetDownloadOverageBillingModeAsOwnerHandler(ISiteRepository sites, IClock clock)
{
    /// <summary>Reused, not reinvented - the identical bound
    /// <c>SetDownloadBlockExemptionAsOwnerHandler.MaxReasonLength</c> already carries, which is itself
    /// <c>RevokeModuleForSiteAsOwnerHandler.MaxReasonLength</c>.</summary>
    public const int MaxReasonLength =
        SetDownloadBlockExemptionAsOwner.SetDownloadBlockExemptionAsOwnerHandler.MaxReasonLength;

    public async Task<Result> HandleAsync(SetDownloadOverageBillingModeAsOwner command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return ConversationErrors.DownloadOverageBillingModeReasonRequired(
                "A reason is required whenever a tenant's download-overage billing mode changes - state why.");
        }

        if (command.Reason.Trim().Length > MaxReasonLength)
        {
            return ConversationErrors.DownloadOverageBillingModeReasonRequired(
                $"A download-overage billing mode reason cannot exceed {MaxReasonLength} characters.");
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        site.SetDownloadOverageBillingMode(command.Mode, command.SetBy, command.Reason.Trim(), clock.UtcNow);
        await sites.SaveAsync(site, cancellationToken);

        return Result.Success();
    }
}
