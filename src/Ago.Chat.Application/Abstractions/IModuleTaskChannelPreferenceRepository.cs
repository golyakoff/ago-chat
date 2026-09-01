using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-11`: the per-booking priority list's own port. Shaped by its one real use case - "read the
/// current list" and "replace it wholesale" - rather than generic Save/Delete primitives, the same
/// "ports are shaped by the use case, not by the storage engine" rule `clean-architecture.md` states.
/// <see cref="ReplaceForModuleTaskAsync"/> exists as one method, not a Delete-then-Save pair the
/// Application handler would have to compose itself, because "the visitor's list for this booking is
/// now exactly this" is the actual write this system ever performs - there is no partial-update use case
/// (no "move item 2 up one slot") for this to leak.
/// </summary>
public interface IModuleTaskChannelPreferenceRepository
{
    /// <summary>Ordered by <see cref="ModuleTaskChannelPreference.Priority"/> ascending (1 = highest).</summary>
    Task<IReadOnlyList<ModuleTaskChannelPreference>> ListForModuleTaskAsync(
        ModuleTaskId moduleTaskId, CancellationToken cancellationToken);

    /// <summary>Atomically replaces every row for <paramref name="moduleTaskId"/> with
    /// <paramref name="preferences"/> - an empty list clears it, the same "explicit empty means back to
    /// automatic" shape `14-13`'s own <c>SetPreferredChannelIdentity</c>'s <c>null</c> uses for its
    /// single-value preference.</summary>
    Task ReplaceForModuleTaskAsync(
        ModuleTaskId moduleTaskId, IReadOnlyList<ModuleTaskChannelPreference> preferences, CancellationToken cancellationToken);
}
