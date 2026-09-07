using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantModuleQuantityAsOwner;

/// <summary>
/// `23-66`: the platform owner's own half of `22-07`'s crossing - the write that had a store
/// (<see cref="IModuleQuantityGrantStore"/>) and a consumer (`ago-calendar`'s own
/// `ModuleQuantityGrantedConsumer`) but no way to be called at all. Never asks the module anything -
/// rule 8 forbids the calendar (or any module) being asked at write time whether it may create the
/// next worker, so this handler's whole job, like its tenant-facing sibling's, is making the granted
/// number durable on this side and telling the module it changed.
/// </summary>
public sealed class GrantModuleQuantityAsOwnerHandler(IModuleQuantityGrantStore grants, IClock clock)
{
    public async Task<Result> HandleAsync(GrantModuleQuantityAsOwner command, CancellationToken cancellationToken)
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

        if (command.Quantity < 0)
        {
            return ConversationErrors.ModuleInvalid("A granted quantity cannot be negative.");
        }

        await grants.GrantAsync(command.SiteId, moduleKey, command.Quantity, clock.UtcNow, cancellationToken);
        return Result.Success();
    }
}
