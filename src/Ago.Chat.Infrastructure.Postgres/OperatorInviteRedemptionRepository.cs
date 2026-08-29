using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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
public sealed class OperatorInviteRedemptionRepository(AgoChatDbContext db, IIdGenerator idGenerator)
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

        var seatLimit = await LockSiteAndReadSeatLimitAsync(invite.SiteId, cancellationToken);
        // `13-03`: `AND removed_at IS NULL` added - a real, necessary fix to this already-shipped
        // query, named explicitly in `13-03`'s own backlog rather than rediscovered as a surprise.
        // Without it, a removed operator counted against this site's seat limit forever, since nothing
        // before this item ever gave `operators` a row a real removal could set. `HoldsSeat` is
        // deliberately not part of this filter - this count answers "how many operator rows does this
        // site have", the input `13-01`'s own seat-limit check was always about, not "how many
        // currently hold an assigned seat" (`GetSeatAssignmentSummaryHandler`'s own, different
        // question).
        var operatorCount = await db.Operators.CountAsync(o => o.SiteId == invite.SiteId && o.RemovedAt == null, cancellationToken);
        if (operatorCount >= seatLimit)
        {
            // Rolled back, nothing committed - the invite stays exactly as it was. `13-01`'s own
            // Done-when: "a capacity-rejected invite is confirmed still redeemable afterward once a
            // seat opens up" - true here by construction, since this method never staged a single
            // change against it on this path.
            await transaction.RollbackAsync(cancellationToken);
            return new OperatorInviteRedemptionResult.SeatLimitReached(seatLimit);
        }

        var now = attempt.Now;
        var newOperatorId = new OperatorId(idGenerator.NewId(now));
        // Capacity 5, Offline - the identical starting shape `RegisterSiteHandler` gives a freshly
        // bootstrapped site's own first operator; an invited operator is not structurally different
        // from a self-registered one once the row exists.
        db.Operators.Add(new Operator(newOperatorId, invite.SiteId, OperatorStatus.Offline, capacity: 5, attempt.ExternalSubjectId));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = newOperatorId, RoleId = invite.RoleId });
        invite.Redeem(newOperatorId, now);

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

    private async Task<int> LockSiteAndReadSeatLimitAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand("SELECT seat_limit FROM sites WHERE id = @siteId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            // A foreign key (OperatorInviteConfiguration.HasOne<Site>) should make this unreachable -
            // an invite cannot exist for a site row that has been deleted out from under it, and this
            // codebase has no site-deletion path at all. Thrown, not translated into a
            // OperatorInviteRedemptionResult case, because a caller has no legal recourse for "the site
            // this invite names does not exist" the way it does for every other outcome above.
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while redeeming an operator invite - a foreign key should have prevented this.");
        }

        return (int)result;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
