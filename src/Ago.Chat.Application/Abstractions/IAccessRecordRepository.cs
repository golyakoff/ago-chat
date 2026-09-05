using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `24-12`: the write side of "who read a person's data" - one row per boundary-crossing access,
/// mirroring <see cref="IErasureRequestRepository"/>/<see cref="IExportRequestRepository"/>'s own
/// family (a receipt with no aggregate behind it, raw Npgsql end to end - <c>AccessRecordRepository</c>'s
/// own remarks give the identical "no business invariant beyond one row per event" reasoning
/// <see cref="IExportRequestRepository"/>'s own remarks already give for itself).
///
/// <para><b>Two callers at two different layers, deliberately.</b> <c>GetVisitorHistoryHandler</c>
/// (`Ago.Chat.Application`) calls <see cref="RecordAsync"/> directly - the acting operator's id is
/// already a field on its own command, the same layer that made the authorization decision this row
/// is evidence of. The platform-owner endpoints (`Ago.Chat.Api`) call it too, but from the endpoint
/// delegate itself, never through a command field - <see cref="AccessRecordActorKind"/>'s own remarks
/// explain why a Keycloak `sub` claim is a transport-layer fact for an actor `adr/0032` gives no
/// domain identifier at all, the identical layering `ListSitesForOwnerHandler` already draws for the
/// authorization question. Both are legal callers of the same port: a Minimal API delegate is a host,
/// and a host may depend on anything (`CLAUDE.md` rule 1).</para>
///
/// <para><b>Only a successful, boundary-crossing access ever calls this.</b> A caller refused by
/// <c>IPermissionChecker</c>, `RequirePlatformOwner`, or a per-conversation comparison never reaches
/// the point where a repository call could be made - nothing here needs to distinguish "denied" from
/// "never asked", because a denied attempt reads no data at all and this table's whole reason to exist
/// is "this data was read", not "this was asked for" (`24-12`'s own "a read that fails authorisation
/// is not an access").</para>
/// </summary>
public interface IAccessRecordRepository
{
    /// <summary>Inserts one <c>access_records</c> row. <paramref name="record"/>'s own <c>Id</c> is
    /// minted by the caller via <c>IIdGenerator</c> - the same "handler generates the id, repository
    /// just persists it" shape every other write in this codebase uses, and the reason keyset paging
    /// in <see cref="ListForSiteAsync"/> can page by <c>id</c> at all (`IIdGenerator`'s own contract:
    /// ids sort in generation order).</summary>
    Task RecordAsync(AccessRecordToWrite record, CancellationToken cancellationToken);

    /// <summary>The tenant's own read-back - `24-12`'s own Scope: "reachable by the tenant for their
    /// own site, not only by AGO." Keyset by <c>id</c> descending (newest first), the same convention
    /// <see cref="IConversationReadStore.GetVisitorHistoryAsync"/> already uses; <paramref name="beforeId"/>
    /// <see langword="null"/> means the first page. Scoped to <paramref name="siteId"/> - a tenant
    /// never sees another tenant's rows, including the ones naming the platform owner's own reads of
    /// their site (`24-12`'s own open question, answered here: yes, a tenant sees AGO's accesses to
    /// their own data, because withholding exactly that access is the one thing this table exists to
    /// make impossible to hide).</summary>
    Task<AccessRecordPage> ListForSiteAsync(
        SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// One access to be recorded. Deliberately carries nothing about *what was read* - `24-12`'s own
/// "do not build a second copy of the data. Record that a read happened and by whom - not what was
/// returned", the mirror-image discipline `adr/0112` already applies to <c>erasure_records</c>, applied
/// here to the opposite direction of the same problem (there: do not let a receipt re-identify the
/// person whose data is gone; here: do not let a receipt re-create the data that was read).
///
/// <para><paramref name="SiteId"/> carries no foreign key at the storage layer
/// (<c>AccessRecordEntityConfiguration</c>'s own remarks) - the same <c>adr/0111</c>/<c>adr/0112</c>
/// mechanism reused for a third reason: a record of who read this tenant's data must outlive
/// <c>SiteErasureJob</c>'s own <c>DeleteSiteAsync</c>, or the one tenant a departing customer most
/// wants an honest answer for ("did AGO look at my data before you finished erasing me") is exactly
/// the one whose evidence a cascade would have destroyed first. <see langword="null"/> only for
/// <see cref="AccessRecordKind.OwnerSiteList"/>, whose read spans every tenant at once.</para>
///
/// <para><paramref name="ResourceId"/> is likewise a bare, unconstrained <see cref="Guid"/> rather
/// than a foreign key into whichever table <paramref name="ResourceKind"/> names - for the identical
/// survive-the-subject's-own-erasure reason, restated once more: a conversation named here as the one
/// an operator opened must remain nameable in this row even after that very conversation is later
/// erased, or the accountability this item exists to build would disappear exactly when a person's own
/// erasure request is the reason to ask for it.</para>
/// </summary>
public sealed record AccessRecordToWrite(
    Guid Id,
    DateTimeOffset OccurredAt,
    AccessRecordKind AccessKind,
    SiteId? SiteId,
    AccessRecordActorKind ActorKind,
    string ActorId,
    AccessRecordResourceKind? ResourceKind,
    Guid? ResourceId);

/// <summary>One row, read back for a tenant's own report - the same fields <see cref="AccessRecordToWrite"/>
/// wrote, nothing more (in particular, no content of whatever was read - see that type's own
/// remarks).</summary>
public sealed record AccessRecordItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    AccessRecordKind AccessKind,
    AccessRecordActorKind ActorKind,
    string ActorId,
    AccessRecordResourceKind? ResourceKind,
    Guid? ResourceId);

/// <summary>One keyset page of <see cref="IAccessRecordRepository.ListForSiteAsync"/> - the same
/// shape every other keyset read in this codebase returns (<c>NextBeforeId</c> <see langword="null"/>
/// once the oldest row has been reached).</summary>
public sealed record AccessRecordPage(IReadOnlyList<AccessRecordItem> Items, Guid? NextBeforeId);
