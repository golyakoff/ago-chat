using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`20-11`: <see cref="IModuleTaskChannelPreferenceRepository"/>'s real implementation. Both
/// operations are keyed on <see cref="ModuleTaskId"/> alone (never loaded through
/// <see cref="Conversation"/>'s own encapsulated navigation - this table is a plain, independent set of
/// rows referencing a module task's id by value, the same "aggregates stay independent" shape
/// <see cref="ChannelIdentity"/>'s own repository already uses for its own foreign references).</summary>
public sealed class ModuleTaskChannelPreferenceRepository(AgoChatDbContext db) : IModuleTaskChannelPreferenceRepository
{
    public async Task<IReadOnlyList<ModuleTaskChannelPreference>> ListForModuleTaskAsync(
        ModuleTaskId moduleTaskId, CancellationToken cancellationToken) =>
        await db.ModuleTaskChannelPreferences
            .Where(p => p.ModuleTaskId == moduleTaskId)
            .OrderBy(p => p.Priority)
            .ToListAsync(cancellationToken);

    /// <summary>Delete, then insert - two separate <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// calls, deliberately not one. <c>ux_module_task_channel_preferences_module_task_priority</c>'s own
    /// uniqueness means a single batched <c>SaveChanges</c> risks the new priority-1 row's <c>INSERT</c>
    /// racing the old priority-1 row's still-pending <c>DELETE</c> within the same round trip - EF Core's
    /// own statement ordering across unrelated rows of one table is not a guarantee this repository wants
    /// to depend on. The same "two writes, not one transaction, because the shape does not fit in one"
    /// trade-off `ConfirmPhoneVerificationHandler`'s own remarks accept for consuming a
    /// <see cref="Domain.PendingPhoneVerification"/> and linking a <see cref="ChannelIdentity"/> - a crash
    /// between the two would leave the list briefly empty rather than corrupted, an acceptable gap for a
    /// visitor-set preference that is not itself a write-decision guarantee (`CLAUDE.md` rule 8 governs
    /// compare-and-set reads, not this).</summary>
    public async Task ReplaceForModuleTaskAsync(
        ModuleTaskId moduleTaskId, IReadOnlyList<ModuleTaskChannelPreference> preferences, CancellationToken cancellationToken)
    {
        var existing = await db.ModuleTaskChannelPreferences
            .Where(p => p.ModuleTaskId == moduleTaskId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            db.ModuleTaskChannelPreferences.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (preferences.Count > 0)
        {
            db.ModuleTaskChannelPreferences.AddRange(preferences);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
