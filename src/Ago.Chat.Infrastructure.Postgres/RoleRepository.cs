using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class RoleRepository(AgoChatDbContext db, IIdGenerator idGenerator, IClock clock) : IRoleRepository
{
    public async Task<Guid?> GetIdByNameAsync(SiteId siteId, string name, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.Name == name)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return role;
    }

    /// <summary>
    /// `23-102`: one `UPDATE` doing its own set-union, not a tracked read-modify-write - the same
    /// "compare-and-set inside the database, never in application code" reasoning
    /// `OperatorCapacityStore`/`TagRepository.AddToConversationAsync` already give their own idempotent
    /// writes. `array_agg(DISTINCT ...)` over `permissions || @new` is what makes a repeated call (a
    /// re-grant, a revoke-then-re-grant cycle) add nothing the second time, with no read beforehand and
    /// no race between two concurrent grants naming the same role - Postgres's own row-level lock on the
    /// `UPDATE` serialises them, so there is no window for a "read old value, union, write" pair to lose
    /// one caller's addition to the other's, the failure mode a tracked EF round-trip would have.
    ///
    /// <para><b>`23-104`: the role's own row is not the only thing this write has to change.</b> Every
    /// operator who currently holds <paramref name="roleName"/> on this site enforces against a *copy* of
    /// this permission set - AGO Calendar's own <c>role_assignment_projections</c>, kept current only by
    /// <c>RoleAssignmentsChanged</c>. Before this item, this method touched `roles` and nothing else, so
    /// a module grant seeded the vocabulary chat itself checks and published nothing for the vocabulary
    /// the other product checks - <c>23-104</c>'s own incident. This method now enqueues one
    /// <c>RoleAssignmentsChanged</c> per currently-linked holder of the role, through the same outbox
    /// every other publisher of that event uses (never a direct write into the other product's
    /// database - <c>adr/0093</c>), and does so inside the <i>same</i> transaction as the `UPDATE` above
    /// (CLAUDE.md rule 4): a failure staging those events rolls the permission change back with them,
    /// rather than leaving the role changed with nobody told.</para>
    ///
    /// <para><b>Keyed by role, not by the one operator who happened to trigger the grant.</b> The
    /// projection is keyed by external subject (<see cref="RoleAssignmentsChangedMapper"/>'s own
    /// remarks), and a role's permissions are shared by everyone who holds it - publishing for only the
    /// caller would leave every other holder exactly as stale as `23-104` found this tenant's Workers
    /// screen. <see cref="Domain.Operator.ExternalSubjectId"/> is <see langword="null"/> for an operator
    /// with an unredeemed invite; that operator has no projection row anywhere to correct (the identical
    /// "nothing to project" guard <see cref="RoleAssignmentProjectionBackfill"/>'s own candidate list and
    /// <c>RemoveOperatorHandler</c>'s own publisher both apply), so they are skipped, not thrown for.</para>
    ///
    /// <para><b>Idempotent both ways.</b> A repeated call with the identical permission set still enqueues
    /// one event per holder - deliberately unconditional, the same "restaging identical values is a no-op
    /// in every sense that matters" reasoning <see cref="RoleAssignmentProjectionBackfill"/>'s own remarks
    /// give, since <c>RoleAssignmentProjectionStore.StageAsync</c> is an unconditional full replace with no
    /// duplicate-detection of its own to spare. This is also the four-broken-sites repair path this
    /// method's own `23-102` remarks already describe: a re-grant of an already-granted module re-seeds
    /// identical permissions and now also re-publishes identical facts, which is how a site whose
    /// projection went stale before this item existed gets fixed by an ordinary re-grant rather than a
    /// second, separate tool.</para>
    ///
    /// <para><b>Race-safe against a concurrent removal, the same way <see cref="RoleAssignmentProjectionBackfill.PublishOneAsync"/>
    /// is.</b> The candidate list below is read with no lock; each candidate's own `removed_at`/
    /// `external_subject_id` is re-read under <c>SELECT ... FOR UPDATE</c> immediately before anything is
    /// staged for them - the same "lock the row a decision depends on, read it again under the lock" shape
    /// that method's own remarks describe at length, closing the window where a real, concurrent
    /// <c>RemoveOperatorHandler</c> call has already committed a removal this method's own unlocked read
    /// could not have seen yet. `RemoveOperatorHandler` itself takes no explicit lock on the operator row -
    /// it relies on Postgres's own row-level lock during its `UPDATE`, which this method's `FOR UPDATE`
    /// blocks on and then reads past, the identical mechanism that method's own remarks rely on.</para>
    /// </summary>
    public async Task AddPermissionsAsync(
        SiteId siteId, string roleName, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        if (permissions.Count == 0)
        {
            // Nothing to add - the identical "empty means nothing to do" shape
            // IModulePermissionsProvider.Get returns for a module the deployment declared no extra
            // permissions for. Skipped before ever reaching the database, not sent as a no-op UPDATE,
            // and before anything is published: nothing changed, so nobody has anything to learn.
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var newPermissions = permissions.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            update roles
            set permissions = (select array_agg(distinct p) from unnest(permissions || {newPermissions}) as p)
            where site_id = {siteId.Value} and name = {roleName}
            """,
            cancellationToken);

        var roleId = await db.Roles.AsNoTracking()
            .Where(r => r.SiteId == siteId && r.Name == roleName)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (roleId is null)
        {
            // No role by that name on this site - the `UPDATE` above touched zero rows, and there is
            // no role for any operator to hold, so there is nothing for anyone's projection to learn.
            // Committed rather than rolled back only because nothing was ever staged to roll back.
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Unlocked candidate list - every operator on this site who currently holds this role and has a
        // linked external identity. Each one is re-checked under a row lock immediately below, so a
        // stale entry here (a removal that commits after this read) is caught before anything is staged
        // for it, not trusted.
        var candidateOperatorIds = await db.OperatorRoles
            .Where(link => link.RoleId == roleId.Value)
            .Join(db.Operators, link => link.OperatorId, o => o.Id, (link, o) => o)
            .Where(o => o.SiteId == siteId && o.RemovedAt == null && o.ExternalSubjectId != null)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        if (candidateOperatorIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        // Read once, applied to every event this call stages - the same "one call, one timestamp"
        // reasoning RoleAssignmentProjectionBackfill.RunAsync's own remarks give, for the identical
        // ordering-correctness reason: every row this call stages must sort, in the outbox's own
        // occurred_at order, before any genuinely-concurrent real event whose own timestamp is read at
        // or after this instant.
        var occurredAt = clock.UtcNow;
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);

        foreach (var operatorId in candidateOperatorIds)
        {
            string? externalSubjectId;
            bool isRemoved;

            await using (var lockCommand = new NpgsqlCommand(
                "SELECT external_subject_id, removed_at FROM operators WHERE id = @id FOR UPDATE",
                connection, npgsqlTransaction))
            {
                lockCommand.Parameters.AddWithValue("id", operatorId.Value);
                await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    // No delete path exists for `operators` anywhere in this codebase - unreachable in
                    // ordinary operation, the same standing as the analogous branch in
                    // RoleAssignmentProjectionBackfill.PublishOneAsync.
                    continue;
                }

                externalSubjectId = reader.IsDBNull(0) ? null : reader.GetString(0);
                isRemoved = !reader.IsDBNull(1);
            }

            if (isRemoved || externalSubjectId is null)
            {
                // A concurrent removal committed between the unlocked candidate read above and this
                // lock - correctly left unpublished here, because that removal's own publisher
                // (RemoveOperatorHandler) already is, or is about to be, the truthful fact for this
                // subject.
                continue;
            }

            // The full set this operator now enforces against, not only what this call just added -
            // an operator holding both seeded roles (the account owner, SiteRegistrationRepository's own
            // shape) must project the union, and RoleAssignmentsChanged always carries a full snapshot,
            // never a delta.
            var rolePermissions = await db.OperatorRoles
                .Where(link => link.OperatorId == operatorId)
                .Join(db.Roles, link => link.RoleId, role => role.Id, (link, role) => role.Permissions)
                .ToListAsync(cancellationToken);
            var allPermissions = rolePermissions.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToList();

            outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
                externalSubjectId, siteId.Value, allPermissions, occurredAt, idGenerator));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
