using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: the one type in this system that changes the schema. `adr/0056`: "a migration is applied by
/// its own deployable, and by nothing else" - <c>SchemaMigrationTests</c> is what makes the "nothing
/// else" half true rather than intended, by failing if any serving host so much as references this
/// type.
///
/// <para><b>Forward only, deliberately.</b> There is no <c>Down</c>, no <c>--target</c> and no
/// rollback here, and that is a decision rather than an omission (`adr/0056`). EF generates
/// <c>Down()</c> methods and this project has never executed one; a rollback path nobody has tested
/// is worse than none, because it will be believed at exactly the moment it matters. A migration that
/// turns out to be wrong is `15-02`'s restore.</para>
///
/// <para><b>Idempotent with no new mechanism.</b> <c>__EFMigrationsHistory</c> already records what
/// has been applied, so a second run applies nothing and reports nothing applied. That is what lets
/// the same Job run on every deploy rather than only on deploys that happen to need it - and a
/// conditional deploy step is exactly the kind of thing that gets skipped (the 2026-08-25
/// incident).</para>
/// </summary>
public sealed class SchemaMigrationApplier(AgoChatDbContext db, SchemaVersionCheck check)
{
    /// <summary>
    /// Applies every pending migration and reports the state on both sides of the call.
    ///
    /// <para>The "before" status is captured first so the caller can name what it applied rather than
    /// only that it finished. `8-08`'s Scope is explicit that a migration which runs silently is the
    /// same operational problem as one that does not run: the 2026-08-25 incident was invisible
    /// precisely because nothing said anything either way.</para>
    ///
    /// <para>No transaction is opened here. EF wraps each migration in its own, and Postgres
    /// executes DDL transactionally, so a failing migration leaves the ones before it applied and
    /// itself rolled back - which is the state the history table then correctly describes. Wrapping
    /// the whole run in one outer transaction would deadlock against migrations that manage their own
    /// (`Stage2PartitionMessages` runs raw SQL) and would buy an all-or-nothing property this project
    /// has never needed.</para>
    /// </summary>
    public async Task<SchemaMigrationOutcome> ApplyAsync(CancellationToken cancellationToken)
    {
        var before = await check.InspectAsync(cancellationToken);
        if (before.IsCurrent)
        {
            return new SchemaMigrationOutcome(before, before, []);
        }

        await db.Database.MigrateAsync(cancellationToken);

        var after = await check.InspectAsync(cancellationToken);
        return new SchemaMigrationOutcome(before, after, before.Pending);
    }
}

/// <summary>What one run of <see cref="SchemaMigrationApplier.ApplyAsync"/> did.
/// <paramref name="Applied"/> is empty for the no-op second run, which is the state that proves
/// idempotency rather than assuming it.</summary>
public sealed record SchemaMigrationOutcome(
    SchemaStatus Before, SchemaStatus After, IReadOnlyList<string> Applied);
