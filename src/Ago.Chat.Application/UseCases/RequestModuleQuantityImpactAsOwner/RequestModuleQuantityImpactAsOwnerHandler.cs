using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RequestModuleQuantityImpactAsOwner;

/// <summary>
/// `23-88`: the platform owner's own half of the async preview round trip - the write that stages a
/// question and its outbox row (<see cref="IModuleQuantityImpactPreviewStore.RequestAsync"/>), the
/// identical shape <see cref="GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler"/> already
/// establishes for the sibling grant. Returns as soon as chat's own row and outbox entry commit -
/// never waits for, and makes no promise about, the module's own answer.
/// </summary>
public sealed class RequestModuleQuantityImpactAsOwnerHandler(IModuleQuantityImpactPreviewStore previews, IClock clock)
{
    public async Task<Result> HandleAsync(RequestModuleQuantityImpactAsOwner command, CancellationToken cancellationToken)
    {
        ModuleKey moduleKey;
        try
        {
            moduleKey = new ModuleKey(command.ModuleKey);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        if (command.RequestedQuantity < 0)
        {
            return ConversationErrors.ModuleInvalid("A requested quantity cannot be negative.");
        }

        await previews.RequestAsync(command.SiteId, moduleKey, command.RequestedQuantity, clock.UtcNow, cancellationToken);
        return Result.Success();
    }
}
