using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-08`: "who did it, when, why, and until when" - the same standard `adr/0118` already holds a
/// forced module revoke to (<see cref="IModuleRevokeOverrideRepository"/>), and the same "written
/// once, by the handler that decided the change, never touched again" write-only shape
/// <see cref="IRoleChangeRecordRepository"/> already establishes for its own family. A deliberately
/// separate table (<c>site_suspensions</c>), not a widened <c>role_change_records</c> or
/// <c>access_records</c>: a suspension act is neither a role change nor a platform-owner *read* across
/// a tenant boundary (`AccessRecordKind`'s own remarks on why that enum stays the defensible set it
/// names), it is its own asymmetric power with its own shape - one row per act (suspend, extend, or
/// lift), never updated once written, so a site's own suspension history is exactly its own rows in
/// <c>performed_at</c> order.
///
/// <para><b>Must run inside the caller's own ambient transaction.</b> The identical contract
/// <see cref="IRoleChangeRecordRepository"/> states for itself: this record commits or rolls back
/// atomically with the <see cref="Site"/> write it documents, so every handler that calls this also
/// wraps <see cref="ISiteRepository.SaveAsync"/> and this call in one <c>IUnitOfWork</c>-scoped
/// transaction, the same shape <c>ChangeOperatorRoleHandler</c> already uses for its own paired
/// writes.</para>
///
/// <para><b>Write-only today, deliberately</b> - the identical "a read side is real, buildable
/// follow-up once an actual caller needs one" judgement <see cref="IRoleChangeRecordRepository"/>'s own
/// remarks state, restated here. `22-08`'s own console screen ("who is currently suspended") is served
/// by <see cref="ISiteSuspensionReadStore.ListForOwnerAsync"/> instead, which reads this table's *most
/// recent* row per site - the only read this item's own Done-when needs; a full per-site history
/// browser is real, unscoped follow-up, not something this item's own Done-when asks for.</para>
/// </summary>
public interface ISiteSuspensionRecordRepository
{
    Task RecordAsync(SiteSuspensionRecordToWrite record, CancellationToken cancellationToken);
}

/// <summary>One suspension act to be recorded. <paramref name="PerformedBy"/> is the platform owner's
/// own Keycloak <c>sub</c> claim, the identical "recorded, never authorising" shape
/// <c>RevokeModuleForSiteAsOwner.RevokedBy</c>'s own remarks give for the analogous field - the
/// platform owner carries no <see cref="OperatorId"/> a domain type could name them by
/// (`adr/0032`).</summary>
public sealed record SiteSuspensionRecordToWrite(
    Guid Id,
    SiteId SiteId,
    string Action,
    string PerformedBy,
    string Reason,
    DateTimeOffset? SuspendedUntil,
    DateTimeOffset PerformedAt);
