using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantModuleQuantity;

/// <summary>
/// `22-07`/`adr/0093`: the one write the crossing depends on. Rule 8 forbids the calendar (or any
/// module) asking chat at write time whether it may create the next worker, so this handler's whole
/// job is making the granted number durable on this side and telling the module it changed - never
/// calling the module directly. See <see cref="IModuleQuantityGrantStore.GrantAsync"/> for why the
/// state change and the outbox row are one write rather than two.
/// </summary>
public sealed class GrantModuleQuantityHandler(
    IModuleQuantityGrantStore grants, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(GrantModuleQuantity command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to configure this site's modules.");
        }

        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (command.Quantity < 0)
        {
            return ConversationErrors.ModuleInvalid("A granted quantity cannot be negative.");
        }

        await grants.GrantAsync(command.SiteId, moduleKey, command.Quantity, clock.UtcNow, cancellationToken);
        return Result.Success();
    }
}
