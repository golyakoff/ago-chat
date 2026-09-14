using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AiAddOn;

/// <summary>
/// `25-04`: turn the add-on off. The agreement's own point 6 - "отключить модуль можно в любой момент" -
/// so this has exactly one precondition (<see cref="Permission.SiteConfigure"/>) and never asks whether
/// the tenant is still paying or still accepts anything: a stop must never itself be blocked.
///
/// <para><b>A site with no row is already off, and that is a success, not a 404.</b> Returning an error
/// for "disable something that was never on" would push a console into distinguishing two states that
/// mean the same thing to a tenant; this returns the same "it is off" answer either way, and writes
/// nothing when there was nothing to write.</para>
/// </summary>
public sealed class DisableAiAddOnHandler(
    IPermissionChecker permissions,
    IAiAddOnEnablementRepository enablements,
    IClock clock)
{
    public async Task<Result<AiAddOnDisabled>> HandleAsync(DisableAiAddOn command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.DisabledBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return AiAddOnErrors.Forbidden("Operator does not have permission to configure this site.");
        }

        var now = clock.UtcNow;
        var enablement = await enablements.GetForSiteAsync(command.SiteId, cancellationToken);
        if (enablement is null)
        {
            return new AiAddOnDisabled(command.SiteId.Value, DisabledAt: null);
        }

        enablement.Disable(command.DisabledBy, now);
        await enablements.SaveAsync(enablement, cancellationToken);

        return new AiAddOnDisabled(command.SiteId.Value, now);
    }
}

/// <summary><paramref name="DisabledAt"/> is <see langword="null"/> when there was nothing to turn
/// off - see the handler's own remarks for why that is a success.</summary>
public sealed record AiAddOnDisabled(Guid SiteId, DateTimeOffset? DisabledAt);
