using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-01`'s own Context note, restated because this class is where the choice actually lives:
/// `data-model.md`'s `active_chats` shadow property uses a denormalized counter with an atomic
/// `UPDATE ... WHERE ... &lt; capacity` because operator assignment is a high-frequency, contended path
/// where a per-row lock would itself become the bottleneck. Operator invitation is the opposite - rare,
/// low-contention, at most a handful of calls ever per site - so this locks the `sites` row directly
/// (<c>SELECT seat_limit FROM sites WHERE id = @siteId FOR UPDATE</c>) and counts real `operators` rows
/// inside that lock, rather than adding a second denormalized counter that would need a symmetric
/// decrement path that does not exist yet (`13-01`'s own Out of scope: no operator-removal flow exists
/// anywhere in this codebase today).
///
/// <para><b>Why raw SQL for the lock, through the same <see cref="AgoChatDbContext"/> connection.</b>
/// EF has no LINQ shape for <c>FOR UPDATE</c> on a scalar read, and the lock only means anything if the
/// count read and the eventual `operators` insert happen on the *same* Postgres connection and
/// transaction as the lock itself - the identical reasoning <c>OperatorCapacityStore</c>'s own remarks
/// give for issuing its compare-and-set through <c>ExecuteSqlInterpolatedAsync</c> rather than a
/// separate connection. Unlike that compare-and-set (an `UPDATE` with no result set),
/// <see cref="LockSiteAndReadSeatLimitAsync"/> needs a value back, so this reaches for
/// <see cref="AgoChatDbContext.Database"/>'s own open <see cref="NpgsqlConnection"/>/
/// <see cref="NpgsqlTransaction"/> directly with a plain <see cref="NpgsqlCommand"/> - the same
/// raw-Npgsql-inside-an-EF-transaction shape `4-02`'s <c>SkipLockedAssignmentClaimer</c> established for
/// its own <c>Database.UseTransactionAsync</c> combination, mirrored here from the opposite direction
/// (an EF-opened transaction lending its connection to raw SQL, rather than a raw transaction lending
/// itself to EF).</para>
/// </summary>
public sealed class OperatorInviteRedemptionRepository(AgoChatDbContext db, IIdGenerator idGenerator, IOutboxWriter outbox)
    : IOperatorInviteRedemptionRepository
{
    public async Task<OperatorInviteRedemptionResult> RedeemAsync(
        RedeemOperatorInviteAttempt attempt, CancellationToken cancellationToken)
    {
        var invite = await db.OperatorInvites.FirstOrDefaultAsync(i => i.CodeHash == attempt.CodeHash, cancellationToken);
        if (invite is null)
        {
            return new OperatorInviteRedemptionResult.NotFound();
        }

        // Checked before any lock is taken - both are terminal, static facts about this row that
        // cannot become false by waiting (unlike "already operator on site" or the seat count, neither
        // of which is safe to trust without the lock below). A genuine race on *this* exact check
        // (two concurrent redemptions of the identical code) is still caught, just later - see the
        // `DbUpdateConcurrencyException` catch below, which is the real backstop `xmin` provides.
        if (invite.IsRedeemed)
        {
            return new OperatorInviteRedemptionResult.AlreadyRedeemed();
        }

        if (invite.IsExpired(attempt.Now))
        {
            return new OperatorInviteRedemptionResult.Expired();
        }

        // `13-07`/`adr/0068`'s own adjustment: only this invite's own site, never "anywhere" - the
        // older, superseded rule `13-01`'s own backlog note was corrected away from once `13-07`
        // shipped (composite `(external_subject_id, site_id)` uniqueness, not global).
        var alreadyOperatorHere = await db.Operators.AnyAsync(
            o => o.ExternalSubjectId == attempt.ExternalSubjectId && o.SiteId == invite.SiteId, cancellationToken);
        if (alreadyOperatorHere)
        {
            return new OperatorInviteRedemptionResult.AlreadyOperatorOnSite();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var capacity = await LockSiteAndReadCapacityAsync(invite.SiteId, cancellationToken);

        // `25-25`: which of the two independent limits this invite's own role counts against - never
        // both, and (since `25-25`) no longer "seat_limit, regardless of role" the way it was before
        // this item. `AdminRoleName` is the same bare literal `CreateOperatorInviteHandler`/
        // `ChangeOperatorRoleHandler` already compare role names against - no named-role catalogue
        // exists yet for this codebase to reach for instead (`IRoleRepository`'s own remarks: "the only
        // names any site has today").
        var isAdminInvite = await db.Roles.AsNoTracking()
            .Where(r => r.Id == invite.RoleId)
            .Select(r => r.Name == AdminRoleName)
            .SingleAsync(cancellationToken);

        if (isAdminInvite)
        {
            // `25-25`: how many non-removed Administrators this site already has, counted the same
            // "join operator_roles/roles, filter removed_at" shape `OperatorRoleRepository.
            // CountNonRemovedHoldersAsync` uses for the identical question against an arbitrary site -
            // narrowed to `invite.RoleId` directly rather than a second by-name lookup, since this
            // invite's own role id already *is* this site's "Admin" role.
            var adminCount = await db.Operators
                .Where(o => o.SiteId == invite.SiteId && o.RemovedAt == null)
                .Where(o => db.OperatorRoles.Any(or => or.OperatorId == o.Id && or.RoleId == invite.RoleId))
                .CountAsync(cancellationToken);
            if (adminCount >= capacity.AdminLimit)
            {
                // Rolled back, nothing committed - the invite stays exactly as it was, the identical
                // "still redeemable once room opens up" guarantee `13-01`'s own Done-when already gives
                // SeatLimitReached below.
                await transaction.RollbackAsync(cancellationToken);
                return new OperatorInviteRedemptionResult.AdminLimitReached(capacity.AdminLimit);
            }
        }
        else
        {
            // `13-03`: `AND removed_at IS NULL` added - a real, necessary fix to this already-shipped
            // query, named explicitly in `13-03`'s own backlog rather than rediscovered as a surprise.
            // Without it, a removed operator counted against this site's seat limit forever, since
            // nothing before that item ever gave `operators` a row a real removal could set.
            //
            // `25-25`: `AND holds_seat` added - a genuine change of behaviour, not a restatement.
            // Before this item, every redeemed invite counted here regardless of role, because every
            // redeemed invite also defaulted to `HoldsSeat = true`; an Administrator invite (this
            // branch's own `else`) never reaches this count at all, so the filter this count needs is
            // now "how many currently hold an assigned seat" - `IOperatorRepository.
            // CountHeldSeatsAsync`'s own question, restated here rather than reused because that method
            // has no lock of its own and this count must run inside the lock `LockSiteAndReadCapacityAsync`
            // above already took.
            var heldSeats = await db.Operators.CountAsync(
                o => o.SiteId == invite.SiteId && o.RemovedAt == null && o.HoldsSeat, cancellationToken);
            if (heldSeats >= capacity.SeatLimit)
            {
                // Rolled back, nothing committed - the invite stays exactly as it was. `13-01`'s own
                // Done-when: "a capacity-rejected invite is confirmed still redeemable afterward once a
                // seat opens up" - true here by construction, since this method never staged a single
                // change against it on this path.
                await transaction.RollbackAsync(cancellationToken);
                return new OperatorInviteRedemptionResult.SeatLimitReached(capacity.SeatLimit);
            }
        }

        var now = attempt.Now;
        var newOperatorId = new OperatorId(idGenerator.NewId(now));
        // Capacity 5, Offline - the identical starting shape `RegisterSiteHandler` gives a freshly
        // bootstrapped site's own first operator; an invited operator is not structurally different
        // from a self-registered one once the row exists.
        //
        // `25-25`: `holdsSeat: !isAdminInvite` - a genuine change from this constructor's previous,
        // implicit `true` default. `decisions/0006`/`23-71`: an administrator is additional to the
        // paid seats and consumes none of them, a fact `23-71`/`23-72` already gave the account's own
        // founder and an existing colleague promoted via `ChangeOperatorRoleHandler` (neither of which
        // ever touches `HoldsSeat`) but never gave a *freshly invited* Administrator, who had no
        // existing row for anything to leave untouched. An ordinary Operator invite keeps the exact
        // default it always had.
        db.Operators.Add(new Operator(
            newOperatorId, invite.SiteId, OperatorStatus.Offline, capacity: 5, attempt.ExternalSubjectId,
            displayName: attempt.Name, email: attempt.Email, holdsSeat: !isAdminInvite));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = newOperatorId, RoleId = invite.RoleId });
        invite.Redeem(newOperatorId, now);

        // `22-05`/`adr/0093`: the redeemed operator's projected fact - the one role this invite names.
        // Read back from `roles` rather than carried on the invite itself: the invite only ever held a
        // `RoleId` (`OperatorInvite`'s own shape), and the row it names was already committed at site
        // registration, so this is an ordinary read of already-durable data, not a read of anything
        // this method is itself in the middle of writing.
        var rolePermissions = await db.Roles.Where(r => r.Id == invite.RoleId).Select(r => r.Permissions).SingleAsync(cancellationToken);
        outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
            attempt.ExternalSubjectId, invite.SiteId.Value, rolePermissions, now, idGenerator));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else redeemed this exact invite between this method's own pre-lock read above
            // and this save - `OperatorInvite`'s `xmin` caught it. ChangeTracker.Clear() matches
            // ConversationRepository.SaveAsync's own remarks: a failed save leaves every entity staged
            // here (the new Operator/OperatorRoleRecord included) tracked as pending inserts that never
            // actually committed, which a caller must not carry into anything else on this DbContext.
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new OperatorInviteRedemptionResult.AlreadyRedeemed();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The identical identity redeemed a *different* invite for the same site in the narrow
            // window between this method's own pre-lock existence check and this save - caught here by
            // `operators`' own composite `(external_subject_id, site_id)` index (`13-07`/`adr/0068`),
            // the same "let a real constraint be the source of truth for a compare-and-set decision"
            // shape `SiteRegistrationRepository`'s own remarks describe.
            db.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new OperatorInviteRedemptionResult.AlreadyOperatorOnSite();
        }

        await transaction.CommitAsync(cancellationToken);
        return new OperatorInviteRedemptionResult.Success(newOperatorId, invite.SiteId);
    }

    /// <summary>`25-25`: the site's own name for the role `ago-business` decisions `0011`/`0012` price
    /// as "Administrator" - the same bare literal `RegisterSiteHandler`/`MintDemoTenantHandler` seed it
    /// under and `CreateOperatorInviteHandler`/`ChangeOperatorRoleHandler` already compare role names
    /// against; no named-role catalogue exists yet for this codebase to reach for instead.</summary>
    private const string AdminRoleName = "Admin";

    /// <summary>`25-25`: reads both of `Site`'s independent capacity ceilings in the one locked
    /// round trip this method already made for `seat_limit` alone before this item - `AdminLimit`
    /// costs nothing extra to read once the row is already locked for `SeatLimit`'s own sake, and a
    /// second, separately-locked read would only double the round trips for no benefit (the two limits
    /// are read together, never compared against each other).</summary>
    private sealed record SiteCapacity(int SeatLimit, int AdminLimit);

    private async Task<SiteCapacity> LockSiteAndReadCapacityAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand(
            "SELECT seat_limit, admin_limit FROM sites WHERE id = @siteId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // A foreign key (OperatorInviteConfiguration.HasOne<Site>) should make this unreachable -
            // an invite cannot exist for a site row that has been deleted out from under it, and this
            // codebase has no site-deletion path at all. Thrown, not translated into a
            // OperatorInviteRedemptionResult case, because a caller has no legal recourse for "the site
            // this invite names does not exist" the way it does for every other outcome above.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while redeeming an operator invite - a foreign key should have prevented this.");
        }

        return new SiteCapacity(reader.GetInt32(0), reader.GetInt32(1));
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
