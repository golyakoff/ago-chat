using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetModuleTriggerWordsForSite;

/// <summary>
/// `26-320`: replaces an enabled module's trigger words for the caller's own site - see
/// <see cref="SetModuleTriggerWordsForSite"/>'s own remarks for why this is a tenant-facing use case gated
/// by <see cref="IPermissionChecker"/> rather than a second copy of the platform-owner grant.
///
/// <para><b>The two trigger-word validations are the owner path's, reused verbatim - not re-derived.</b>
/// A reserved-word collision (<see cref="ReservedChatCommands.IsReserved"/> →
/// <see cref="ConversationErrors.ModuleTriggerWordReserved"/>) and a cross-module collision with another
/// module enabled on the same site (→ <see cref="ConversationErrors.ModuleTriggerWordAlreadyRegistered"/>)
/// are checked in the identical order, against the identical reads,
/// <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/> and
/// <see cref="EnableModuleForSite.EnableModuleForSiteHandler"/> already use: reserved-word first (a word
/// that will never mean a module trigger on any site, refused before any per-site read), then the overlap
/// loop over the <em>other</em> modules on this site (the module being edited is skipped - two spellings of
/// its own words are the aggregate's own concern, checked when <see cref="EnabledModule.WithTriggerWords"/>
/// re-runs the constructor). The shape/count invariants (non-empty, at most
/// <see cref="EnabledModule.MaxTriggerWords"/>, bounded length, no dup-casing) are the aggregate's, surfaced
/// as <see cref="ConversationErrors.ModuleInvalid"/> the same way every other caller of an
/// <see cref="EnabledModule"/> constructor already does.</para>
///
/// <para><b>Refuses an owner-granted row</b> (<see cref="EnabledModule.GrantedByOwner"/>) with
/// <see cref="ConversationErrors.ModuleTriggerWordsOwnerGrantRefused"/> - the exact mirror of
/// <see cref="DisableModuleForSite.DisableModuleForSiteHandler"/>'s refusal to <em>disable</em> an owner
/// grant: a platform owner who deliberately set a routing string keeps it, and a tenant editing their own
/// settings cannot silently re-point it. And refuses a module this site does not have enabled with
/// <see cref="ConversationErrors.ModuleNotEnabled"/> - there is no row to edit.</para>
///
/// <para><b>No module-side call, no outbox event.</b> Unlike enable/disable, editing trigger words touches
/// nothing outside <c>Ago.Chat</c>: <see cref="ModuleRegistrationTarget"/> carries no trigger words (the
/// module deployment is never told them), and trigger matching is entirely Chat-side -
/// <c>RouteConversationToModuleHandler</c> and the visitor handshake read them live from
/// <see cref="IEnabledModuleReadStore"/> off the row this handler updates (rule 8: a routing decision reads
/// the row, never a cache, so a plain <see cref="IEnabledModuleRepository.UpdateAsync"/> is the whole
/// write). That is why this handler takes neither the registration gateway nor the provisioning secret the
/// enable/disable handlers need - the alternative, re-registering the module on every trigger edit, would be
/// a call to another system for a change that other system does not observe.</para>
/// </summary>
public sealed class SetModuleTriggerWordsForSiteHandler(
    IPermissionChecker permissions, IEnabledModuleRepository modules, IEnabledModuleReadStore moduleReadStore,
    IClock clock)
{
    public async Task<Result> HandleAsync(SetModuleTriggerWordsForSite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden(
                "Operator does not have permission to edit a module's trigger words for this site.");
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

        var existing = await modules.GetAsync(command.SiteId, moduleKey, cancellationToken);
        if (existing is null)
        {
            return ConversationErrors.ModuleNotEnabled();
        }

        // The mirror of DisableModuleForSiteHandler's own owner-grant refusal: an owner-managed module keeps
        // owner-managed triggers. The console reads GrantedByOwner and never offers the field in that state,
        // so this is defence in depth, not the tenant's normal experience.
        if (existing.GrantedByOwner)
        {
            return ConversationErrors.ModuleTriggerWordsOwnerGrantRefused(
                $"Module '{moduleKey.Value}' on this site was enabled by AGO, not from your own settings, so its "
                + "trigger words cannot be edited here. Contact AGO to change them.");
        }

        // The owner path's own two checks, in its own order - see this handler's own remarks.
        var reservedConflict = command.TriggerWords.FirstOrDefault(ReservedChatCommands.IsReserved);
        if (reservedConflict is not null)
        {
            return ConversationErrors.ModuleTriggerWordReserved(reservedConflict);
        }

        var now = clock.UtcNow;
        var existingOnSite = await moduleReadStore.GetForSiteAsync(command.SiteId, now, cancellationToken);
        foreach (var other in existingOnSite)
        {
            if (other.ModuleKey == moduleKey)
            {
                continue;
            }

            var conflictingWord = other.TriggerWords.FirstOrDefault(existingWord =>
                command.TriggerWords.Any(candidate => string.Equals(candidate, existingWord, StringComparison.OrdinalIgnoreCase)));
            if (conflictingWord is not null)
            {
                return ConversationErrors.ModuleTriggerWordAlreadyRegistered(conflictingWord, other.ModuleKey.Value);
            }
        }

        EnabledModule updated;
        try
        {
            updated = existing.WithTriggerWords(command.TriggerWords);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ModuleInvalid(ex.Message);
        }

        await modules.UpdateAsync(updated, cancellationToken);

        return Result.Success();
    }
}
