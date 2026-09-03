using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: the write-side (EF) port for <see cref="EnabledModule"/> - adr/0004's "EF for writes,
/// Dapper for reads" split, mirrored by <see cref="IEnabledModuleReadStore"/> on the read side.
///
/// <para><b>`22-11`: <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> close the "add-and-read
/// only" half of the gap this item's own backlog item names.</b> <see cref="SaveAsync"/>'s own doc
/// comment elsewhere in this codebase called it an upsert, but nothing ever exercised that path - every
/// existing caller mints a fresh <see cref="EnabledModuleId"/>, so <see cref="SaveAsync"/> always
/// inserts in practice. Rotation needs a real update of an existing row by its own id, which is exactly
/// what a detached instance built from <see cref="EnabledModule.WithCredential"/> is - EF's own
/// "insert if detached" branch in <see cref="SaveAsync"/> would try to <c>Add</c> it and collide on the
/// primary key, so <see cref="UpdateAsync"/> exists as an explicit, unambiguous "this is a modification
/// of a row that already exists" call instead.</para>
/// </summary>
public interface IEnabledModuleRepository
{
    Task<EnabledModule?> GetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken);

    Task SaveAsync(EnabledModule module, CancellationToken cancellationToken);

    /// <summary>`22-11`: persists a rotated row - see <see cref="EnabledModule.WithCredential"/> and
    /// this interface's own remarks for why this is not <see cref="SaveAsync"/>.</summary>
    Task UpdateAsync(EnabledModule module, CancellationToken cancellationToken);

    /// <summary>`22-11`: revokes a module for a site outright - deletion, not a soft flag, the same
    /// reasoning `Ago.Calendar.Application.Abstractions.IChatModuleRegistrationRepository.DeleteAsync`'s
    /// own remarks give its sibling.</summary>
    Task DeleteAsync(EnabledModuleId id, CancellationToken cancellationToken);
}
