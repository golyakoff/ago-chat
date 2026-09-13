using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `22-08`: exists solely so <c>dotnet ef migrations add</c> can generate <c>site_suspensions</c>' own
/// <c>CREATE TABLE</c> - the same "migration-scaffolding only, nothing ever queries this DbSet through
/// EF" shape <see cref="RoleChangeRecordEntity"/>'s own remarks establish for their identical write-only
/// family. <see cref="SiteSuspensionRecordRepository"/> writes through raw SQL on the caller's own
/// ambient transaction, never through this entity's change tracking - see that class's own remarks for
/// why - and the one read this table serves
/// (<see cref="Application.Abstractions.ISiteSuspensionReadStore.ListForOwnerAsync"/>) is a hand-written
/// Dapper query, not a LINQ query against this DbSet.
///
/// <para><b>One row per act, never updated.</b> A site's own suspension history is exactly its rows in
/// <see cref="PerformedAt"/> order - suspend, maybe several extends, and either a lift or nothing (a
/// suspension left to expire on its own writes no closing row, the identical "expiry is checked live,
/// never swept" reasoning <see cref="Domain.Site.SuspendedUntil"/>'s own remarks give for why nothing
/// needs to mark that row "closed").</para>
///
/// <para><b>Real foreign key to <see cref="Site"/>, cascading with it</b> - the identical
/// "ordinary tenant-owned data, not evidence of an outside party's access" reasoning
/// <see cref="RoleChangeRecordEntity"/>'s own remarks give for its own identical choice against
/// <c>access_records</c>' deliberately surviving rows: a suspension act's own record has no reason to
/// outlive the tenant it describes.</para>
/// </summary>
internal sealed class SiteSuspensionRecordEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    /// <summary>One of <c>"Suspended"</c>, <c>"Extended"</c>, <c>"Lifted"</c> - a plain string rather
    /// than a <see cref="Domain"/> enum, the identical "the wire/storage carries values, not
    /// vocabulary" discipline <see cref="RoleChangeRecordEntity.NewRoleName"/> already applies to its
    /// own free-standing role name.</summary>
    public string Action { get; set; } = string.Empty;

    public string PerformedBy { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>The resulting value of <see cref="Site.SuspendedUntil"/> after this act -
    /// <see langword="null"/> only for a <c>"Lifted"</c> row.</summary>
    public DateTimeOffset? SuspendedUntil { get; set; }

    public DateTimeOffset PerformedAt { get; set; }
}
