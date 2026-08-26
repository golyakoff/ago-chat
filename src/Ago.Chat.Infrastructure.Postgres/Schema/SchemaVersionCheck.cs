using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: reads the schema's state and changes nothing. Deliberately a separate type from
/// <see cref="SchemaMigrationApplier"/> rather than two methods on one class, because the arch rule
/// this item exists to install (`SchemaMigrationTests`) is "the serving hosts may look, and may not
/// apply" - and a rule about *which type a host references* is one a reviewer can check by reading
/// the using directives, where a rule about which method it calls is not.
///
/// <para>Both types are here, in <c>Infrastructure.Postgres</c>, not in the migrator host: EF Core's
/// migrations API is a persistence concern (`clean-architecture.md` - "Retry, backoff, ... and
/// serialisation belong here"), the three serving hosts need the read half, and the migrator needs
/// both. A type in <c>Ago.Chat.Migrator</c> could not be referenced by the hosts at all - hosts do not
/// reference each other.</para>
/// </summary>
public sealed class SchemaVersionCheck(AgoChatDbContext db)
{
    /// <summary>
    /// Compares <c>__EFMigrationsHistory</c> against the migrations compiled into
    /// <c>Ago.Chat.Infrastructure.Postgres</c>.
    ///
    /// <para><b>Where "the version I expect" comes from.</b> Nowhere but here. Every host already
    /// references this assembly (through <c>Ago.Chat.Module</c>), so every host already carries the
    /// exact list of migrations its own build was compiled against - EF's
    /// <c>IMigrationsAssembly</c> enumerates them from the binary. There is no number in a manifest, no
    /// environment variable, and nothing to keep in sync, which is what makes this immune to the class
    /// of mistake `8-08` exists to prevent: a stated version can drift from the code, and a derived
    /// one cannot.</para>
    ///
    /// <para>Reads only. <c>GetAppliedMigrations</c> issues a <c>SELECT</c> against
    /// <c>__EFMigrationsHistory</c>; <c>GetPendingMigrations</c> is that set subtracted from the
    /// assembly's own. A database with no history table at all reports every migration as pending,
    /// which is the correct answer for an empty database and the reason a first deploy needs no
    /// special case.</para>
    /// </summary>
    public async Task<SchemaStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        var known = db.Database.GetMigrations().ToList();

        return new SchemaStatus(applied, pending, known);
    }
}
