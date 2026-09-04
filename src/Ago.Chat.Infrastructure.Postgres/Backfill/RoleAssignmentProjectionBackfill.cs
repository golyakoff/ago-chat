using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Backfill;

/// <summary>
/// `22-16`/`adr/0093`: the fourth publisher of `RoleAssignmentsChanged`, and the only one not triggered
/// by a request. The other three (`SiteRegistrationRepository`, `OperatorInviteRedemptionRepository`,
/// `RemoveOperatorHandler`) only ever fire on a state change that happens <i>after</i> they exist -
/// none of them can ever fire retroactively for an operator who was already active before `22-05`
/// shipped, which is exactly `22-16`'s gap. This type is a one-shot republish of the current fact for
/// every such operator, through the identical outbox mechanism the other three use - not a direct
/// write into AGO Calendar's own `role_assignment_projections` table, which this repository has no
/// business touching even if it could reach it (`adr/0093`'s domains-stay-apart half; that table lives
/// in a different database this repository has no connection string for). Republishing also proves the
/// publish -&gt; broker -&gt; consume path for real, which a direct write never would - see this item's
/// own report for the fuller argument.
///
/// <para><b>Who counts as a candidate.</b> <c>RemovedAt == null &amp;&amp; ExternalSubjectId != null</c> -
/// the identical guard <see cref="RemoveOperatorHandler"/> and <see cref="OperatorInviteRedemptionRepository"/>'s
/// own publishers already apply (an operator with no linked identity has nothing to project;
/// `RoleAssignmentsChanged`'s own remarks). A removed operator is deliberately excluded rather than
/// republished-as-revoked: if they were removed before `22-05` existed, no publisher ever ran for them
/// either, and no projection row exists anywhere to correct - "absent" already means "holds nothing"
/// on the calendar side (`RoleAssignmentProjectionStore.GetPermissionsAsync`'s own remarks), which is
/// already the right answer for a gone operator. Inventing a second, revocation-shaped rule for that
/// case would be exactly the "second kind of event" `RoleAssignmentsChanged` was designed not to
/// need.</para>
///
/// <para><b>No exclusion for the two tenants `22-05` already projected correctly.</b> Two reasons, not
/// one: first, this repository has no legal way to ask "is this subject already in
/// <c>role_assignment_projections</c>" - that table is AGO Calendar's own, in AGO Calendar's own
/// database, and reading it from here would be exactly the cross-database read `adr/0093` rejected
/// hoisting tenancy to avoid. Second, and the reason it does not matter anyway:
/// <see cref="Ago.Chat.Contracts.RoleAssignmentsChanged"/> carries a full current snapshot, and
/// <c>RoleAssignmentProjectionStore.StageAsync</c> is an unconditional full replace - restaging the
/// identical, still-current permission set for an already-correct tenant writes the identical values
/// back, which is a no-op in every sense that matters (its own remarks: "redelivering the identical
/// message twice stages the identical values twice").</para>
///
/// <para><b>Ordering, and why a concurrent live change cannot land a stale snapshot after a fresh
/// one.</b> Before this type existed, no two publishers of this event could ever race for the same
/// <c>(ExternalSubjectId, SiteId)</c> pair, because each one's own precondition already serialised them
/// - you cannot remove an operator who has not yet redeemed their invite, so
/// <see cref="OperatorInviteRedemptionRepository"/> and <see cref="RemoveOperatorHandler"/> can never
/// fire for the same identity at once. This type is the first publisher that <i>can</i> race a live one
/// - a candidate list built from a plain, unlocked read is stale the instant a real removal commits
/// after it. Two separate mechanisms are at work here, and they earn their keep for two different
/// reasons - <c>RoleAssignmentProjectionBackfillOrderingConcurrencyTests</c> (Concurrency.Tests) is
/// what separated them, by proving one is load-bearing for correctness and the other is not:
/// <list type="number">
/// <item><b>The correctness guarantee.</b> Every event this run publishes carries the <b>same</b>
/// <c>OccurredAt</c> - the wall-clock instant this run started, read once, not once per operator.
/// `Ago.Chat.Worker`'s own <c>OutboxDispatcher</c> claims unpublished rows <c>ORDER BY occurred_at</c>,
/// so a real removal whose own timestamp is read (as <see cref="RemoveOperatorHandler"/> already does,
/// before it ever touches the row) at or after this run's start is guaranteed to sort <i>after</i>
/// every row this run stages - and because <c>RoleAssignmentProjectionStore.StageAsync</c> is an
/// unconditional full replace with no ordering check of its own, "sorts after" already means "wins":
/// the real revoke's later-dispatched empty permission set overwrites whatever this run staged,
/// regardless of which transaction happened to commit to Postgres first. This alone is what the
/// concurrency test proved sufficient - it still held with the row lock below removed and the gap
/// between this method's own read and its own commit deliberately widened to 200ms, precisely because
/// nothing about final projected state depends on commit order once every row in a run shares one
/// timestamp earlier than any genuinely-concurrent real event's own.</item>
/// <item><b>The redundant-publish avoidance, a narrower and separate property.</b>
/// <see cref="PublishOneAsync"/> re-reads <c>removed_at</c>/<c>external_subject_id</c> under a
/// <c>SELECT ... FOR UPDATE</c> on the operator's own row, immediately before staging anything - the
/// same "lock the row a decision depends on, read it again under the lock" shape
/// <c>OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync</c> already established for
/// <c>sites</c>. This does <i>not</i> change whether the final projected state is correct - point 1
/// above already guarantees that on its own - it changes whether this run stages a grant at all for an
/// operator a real, concurrent removal has already committed: without the lock, such a grant would
/// still be staged (the unlocked read simply would not have seen the removal yet), would still
/// correctly lose to the revoke's later timestamp at the consumer, but would sit in the outbox as a
/// confusing, wasted "granted, immediately revoked" pair and would count in
/// <see cref="RoleAssignmentProjectionBackfillOutcome.Published"/> for an operator this run should
/// have reported as skipped. The lock closes that TOCTOU window (the candidate list is read with no
/// lock at all) down to the width of this one transaction.</item>
/// </list>
/// The one gap the lock does not close either: a removal whose <i>unlocked</i> pre-lock read
/// (<c>GetByIdAsync</c> inside <c>RemoveOperatorHandler</c>, which runs before that handler's own
/// timestamp read) already happened in the instant before this run's own start timestamp was captured,
/// but whose commit is still in flight. Point 1 above still keeps the final state correct even then
/// (that removal's own timestamp, read at or after this run's start by construction of "concurrent",
/// still sorts after) - the only cost is the same wasted-publish cosmetic point 2 names. Recorded here
/// rather than assumed away - see this item's own report.</para>
///
/// <para>One transaction per operator, not one for the whole run: a single long transaction spanning
/// every candidate would hold every one of their rows locked for the run's entire duration, blocking
/// unrelated operator management on sites this run has not even reached yet. Per-operator transactions
/// confine the lock to exactly the row being decided, the same granularity every other write in this
/// codebase uses.</para>
/// </summary>
public sealed class RoleAssignmentProjectionBackfill(AgoChatDbContext db, IIdGenerator idGenerator, IClock clock)
{
    /// <summary>
    /// Runs the whole backfill once: builds the candidate list, then publishes (or correctly skips)
    /// one operator at a time, in a stable order (`Id` ascending) rather than whatever order Postgres
    /// happens to return - not a correctness requirement, only a determinism one, so a re-run's own
    /// output is directly comparable line for line.
    /// </summary>
    public async Task<RoleAssignmentProjectionBackfillOutcome> RunAsync(CancellationToken cancellationToken)
    {
        // Read once, applied to every event this run stages - this method's own remarks on ordering
        // above are the reason, not a stylistic choice.
        var runStartedAt = clock.UtcNow;

        var candidateIds = await db.Operators
            .AsNoTracking()
            .Where(o => o.RemovedAt == null && o.ExternalSubjectId != null)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var published = new List<PublishedRoleAssignment>();
        var skippedDueToRace = 0;

        foreach (var operatorId in candidateIds)
        {
            var result = await PublishOneAsync(operatorId, runStartedAt, cancellationToken);
            if (result is { } assignment)
            {
                published.Add(assignment);
            }
            else
            {
                skippedDueToRace++;
            }
        }

        return new RoleAssignmentProjectionBackfillOutcome(candidateIds.Count, published, skippedDueToRace);
    }

    /// <summary>
    /// One operator, one transaction, one outbox row or none. <see langword="internal"/> (not
    /// <see langword="private"/>) so <c>RoleAssignmentProjectionBackfillOrderingConcurrencyTests</c> can
    /// race it directly against a real <see cref="Ago.Chat.Application.UseCases.RemoveOperator.RemoveOperatorHandler"/>
    /// call and prove the lock actually serialises them, rather than trusting this class's own remarks
    /// on faith - the same <c>InternalsVisibleTo("Ago.Chat.Concurrency.Tests")</c> this project already
    /// grants for exactly this purpose.
    /// </summary>
    internal async Task<PublishedRoleAssignment?> PublishOneAsync(
        OperatorId operatorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        string? externalSubjectId;
        Guid siteIdValue;
        bool isRemoved;

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT external_subject_id, site_id, removed_at FROM operators WHERE id = @id FOR UPDATE",
            connection, npgsqlTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", operatorId.Value);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                // No delete path exists for `operators` anywhere in this codebase - unreachable in
                // ordinary operation, same standing as the analogous branch in
                // OperatorInviteRedemptionRepository.LockSiteAndReadSeatLimitAsync, but a re-check
                // under the lock is exactly the kind of read this method must not skip on an
                // assumption.
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            externalSubjectId = reader.IsDBNull(0) ? null : reader.GetString(0);
            siteIdValue = reader.GetGuid(1);
            isRemoved = !reader.IsDBNull(2);
        }

        if (isRemoved || externalSubjectId is null)
        {
            // Re-checked under the lock, not trusted from the candidate list this run built earlier -
            // this class's own remarks on ordering are the reason. A concurrent removal that committed
            // between that list being built and this lock being acquired is correctly seen here, and
            // this operator is correctly left unpublished: the removal's own real-path event already
            // is, or is about to be, the truthful fact for this subject.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var rolePermissions = await db.OperatorRoles
            .Where(link => link.OperatorId == operatorId)
            .Join(db.Roles, link => link.RoleId, role => role.Id, (link, role) => role.Permissions)
            .ToListAsync(cancellationToken);
        var permissions = rolePermissions.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToList();

        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
            externalSubjectId, siteIdValue, permissions, occurredAt, idGenerator));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PublishedRoleAssignment(externalSubjectId, new SiteId(siteIdValue), permissions);
    }
}

/// <summary>One subject/site pair this run staged a <c>RoleAssignmentsChanged</c> event for.</summary>
public sealed record PublishedRoleAssignment(string ExternalSubjectId, SiteId SiteId, IReadOnlyList<string> Permissions);

/// <summary>
/// What one call to <see cref="RoleAssignmentProjectionBackfill.RunAsync"/> did.
/// <paramref name="SkippedDueToRace"/> is expected to be zero in ordinary operation - a positive value
/// means a candidate was removed by a real, concurrent <c>RemoveOperatorHandler</c> call while this ran,
/// which this type's own remarks on ordering say is handled correctly, not that it should not happen.
/// </summary>
public sealed record RoleAssignmentProjectionBackfillOutcome(
    int CandidatesConsidered, IReadOnlyList<PublishedRoleAssignment> Published, int SkippedDueToRace);
