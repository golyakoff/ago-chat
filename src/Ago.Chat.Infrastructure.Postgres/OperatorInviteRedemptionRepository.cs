using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `13-01`'s own Context note, restated because this class is where the choice actually lives:
/// `data-model.md`'s `active_chats` shadow property uses a denormalized counter with an atomic
/// `UPDATE ... WHERE ... &lt; capacity` because operator assignment is a high-frequency, contended path
/// where a per-row lock would itself become the bottleneck. Operator invitation is the opposite - rare,
/// low-contention, at most a handful of calls ever per site - so this locks the `sites` row directly and
/// counts real `operator_roles` rows inside that lock, rather than adding a second denormalized counter
/// that would need a symmetric decrement path that does not exist yet (`13-01`'s own Out of scope: no
/// operator-removal flow exists anywhere in this codebase today).
///
/// <para><b>`25-170`: the lock and the count are no longer this class's own raw SQL.</b> Before this
/// item, this class held its own private `LockSiteAndReadCapacityAsync` (raw Npgsql against
/// <see cref="AgoChatDbContext.Database"/>'s own open connection/transaction - EF has no LINQ shape for
/// `FOR UPDATE`) and two hand-written, role-specific capacity branches beside it. Both are now
/// <see cref="OperatorRoleSeatCapacity"/>'s own job - the unified capacity-check procedure this item's
/// own design calls for, shared with `ChangeOperatorRoleHandler`'s own Admin-promotion guard rather than
/// each keeping a separate copy. This class still opens the ambient transaction that check's own lock
/// participates in (<see cref="RedeemLoadedInviteAsync"/>'s own <c>db.Database.BeginTransactionAsync</c>)
/// and still commits the actual `Operator`/`operator_roles` write inside it - only the lock-and-count
/// step itself moved out.</para>
/// </summary>
public sealed class OperatorInviteRedemptionRepository(
    AgoChatDbContext db, IIdGenerator idGenerator, IOutboxWriter outbox, OperatorRoleSeatCapacity roleSeatCapacity)
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

        // `25-73`: the two new terminal, static facts this item's own design adds - both checked here,
        // before any lock, the identical "cannot become false by waiting" reasoning this method's own
        // remarks already give IsRedeemed/IsExpired above. Revoked is checked before the email match:
        // an admin who revoked an invite meant to withdraw it outright, and a caller presenting the
        // right code with the wrong email should still be told "revoked", not "wrong email", if both are
        // true - the more final fact wins.
        if (invite.IsRevoked)
        {
            return new OperatorInviteRedemptionResult.Revoked();
        }

        // `25-73`'s own real security boundary: the code says which invite, the authenticated caller's
        // own token email says who is actually claiming it, and both must agree. Case-insensitive -
        // email addresses are conventionally case-insensitive in the local part by RFC 5321's own
        // "SHOULD NOT" and universally so in the domain part, and CreateOperatorInviteHandler's own
        // `MailAddress`-normalised value never forced a particular case a real invitee's IdP claim would
        // then have to match byte-for-byte.
        if (attempt.Email is null || !string.Equals(attempt.Email, invite.Email, StringComparison.OrdinalIgnoreCase))
        {
            return new OperatorInviteRedemptionResult.EmailMismatch();
        }

        return await RedeemLoadedInviteAsync(invite, attempt.ExternalSubjectId, attempt.Now, attempt.Name, attempt.Email, cancellationToken);
    }

    /// <summary>`25-85`: the "activate it here" card's own redemption path - see
    /// <see cref="IOperatorInviteRedemptionRepository.RedeemPendingForEmailAsync"/>'s own remarks for
    /// the full security reasoning. No code, no hash lookup: the email itself is the only key, so this
    /// method's own first job is establishing that exactly one live invite answers to it before any of
    /// <see cref="RedeemLoadedInviteAsync"/>'s shared terminal-fact checks run.</summary>
    public async Task<OperatorInviteRedemptionResult> RedeemPendingForEmailAsync(
        RedeemPendingOperatorInviteByEmailAttempt attempt, CancellationToken cancellationToken)
    {
        // The identical "pending" shape `PendingOperatorInviteByEmailReadStore` already queries for
        // `OnboardingPage`'s own steer-away alert (unexpired, unredeemed, unrevoked) - loaded here as
        // full rows rather than an `exists()`, because this call needs to know not just *whether* one
        // exists but *which one*, and whether there is exactly one.
        var candidates = await db.OperatorInvites
            .Where(i => i.Email == attempt.Email && i.RedeemedAt == null && i.RevokedAt == null && i.ExpiresAt > attempt.Now)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return new OperatorInviteRedemptionResult.NotFound();
        }

        if (candidates.Count > 1)
        {
            // `25-85`: a real, if rare, case (`OperatorInvite.Email`'s own remarks: "one email can
            // legitimately hold invites to more than one site at once") - refused rather than guessed,
            // the identical "does not have any legal recourse to a guess" posture `OperatorRoleSeatCapacity`'s
            // own missing-site throw takes for a different genuinely-unreachable situation.
            return new OperatorInviteRedemptionResult.Ambiguous();
        }

        return await RedeemLoadedInviteAsync(candidates[0], attempt.ExternalSubjectId, attempt.Now, attempt.Name, attempt.Email, cancellationToken);
    }

    /// <summary>The shared core both <see cref="RedeemAsync"/> and <see cref="RedeemPendingForEmailAsync"/>
    /// reduce to once they have each found their own single candidate invite by their own different
    /// means (code hash vs. email) and, for <see cref="RedeemAsync"/>, already checked that the presented
    /// email agrees with it - everything from here on (the same-site operator check, the row lock, the
    /// seat/admin capacity check, and the actual `Operator`/`operator_roles` write) is one identical
    /// transaction regardless of how the invite was found, and duplicating it would be exactly the risk
    /// of drift `RedeemOperatorInviteHandler`'s own class-level remarks warn against for a parallel
    /// implementation of a redemption path.</summary>
    private async Task<OperatorInviteRedemptionResult> RedeemLoadedInviteAsync(
        OperatorInvite invite, string externalSubjectId, DateTimeOffset now, string? name, string? email, CancellationToken cancellationToken)
    {
        // `13-07`/`adr/0068`'s own adjustment: only this invite's own site, never "anywhere" - the
        // older, superseded rule `13-01`'s own backlog note was corrected away from once `13-07`
        // shipped (composite `(external_subject_id, site_id)` uniqueness, not global).
        var alreadyOperatorHere = await db.Operators.AnyAsync(
            o => o.ExternalSubjectId == externalSubjectId && o.SiteId == invite.SiteId, cancellationToken);
        if (alreadyOperatorHere)
        {
            return new OperatorInviteRedemptionResult.AlreadyOperatorOnSite();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // `25-25`: which role this invite names, purely to choose the capacity check's own parameter -
        // never both limits, and no longer "seat_limit, regardless of role" the way it was before that
        // item. `25-170`: the two hand-written branches this block replaced (an Admin-role count against
        // `AdminLimit`, a `HoldsSeat`-filtered count against `SeatLimit`) are now one call into
        // `OperatorRoleSeatCapacity` - the identical row-locked primitive `ChangeOperatorRoleHandler`'s
        // own Admin-promotion guard shares, unified rather than duplicated.
        var roleName = await db.Roles.AsNoTracking()
            .Where(r => r.Id == invite.RoleId)
            .Select(r => r.Name)
            .SingleAsync(cancellationToken);

        var check = await roleSeatCapacity.CheckAsync(invite.SiteId, roleName, cancellationToken);
        if (check.IsAtCapacity)
        {
            // Rolled back, nothing committed - the invite stays exactly as it was. `13-01`'s own
            // Done-when: "a capacity-rejected invite is confirmed still redeemable afterward once a
            // seat opens up" - true here by construction, since this method never staged a single
            // change against it on this path.
            await transaction.RollbackAsync(cancellationToken);
            return roleName == AdminRoleName
                ? new OperatorInviteRedemptionResult.AdminLimitReached(check.Limit)
                : new OperatorInviteRedemptionResult.SeatLimitReached(check.Limit);
        }

        var newOperatorId = new OperatorId(idGenerator.NewId(now));
        // Capacity 5, Offline - the identical starting shape `RegisterSiteHandler` gives a freshly
        // bootstrapped site's own first operator; an invited operator is not structurally different
        // from a self-registered one once the row exists.
        db.Operators.Add(new Operator(
            newOperatorId, invite.SiteId, OperatorStatus.Offline, capacity: 5, externalSubjectId,
            displayName: name, email: email));
        // `25-170`: HoldsSeat: true unconditionally - no longer `!isAdminInvite`. Under the pre-`25-170`
        // account-level model, an Administrator invite set the account's own `HoldsSeat` to `false`
        // because the flag meant "occupies an Operator-role seat," which an Administrator never does;
        // now that the flag lives on this specific `(operator, role)` pairing, "holds a seat" means
        // "counts toward this row's own role's own limit" - and every freshly granted role, Operator or
        // Admin alike, starts out counting, the identical "the correct starting state for every row, not
        // a special case" default this table's own migration backfill gives every pre-existing row too
        // (`OperatorRoleRecord`'s own remarks).
        db.OperatorRoles.Add(new OperatorRoleRecord
        {
            OperatorId = newOperatorId,
            RoleId = invite.RoleId,
            HoldsSeat = true,
            GrantedAt = now,
        });
        invite.Redeem(newOperatorId, now);

        // `22-05`/`adr/0093`: the redeemed operator's projected fact - the one role this invite names.
        // Read back from `roles` rather than carried on the invite itself: the invite only ever held a
        // `RoleId` (`OperatorInvite`'s own shape), and the row it names was already committed at site
        // registration, so this is an ordinary read of already-durable data, not a read of anything
        // this method is itself in the middle of writing.
        var rolePermissions = await db.Roles.Where(r => r.Id == invite.RoleId).Select(r => r.Permissions).SingleAsync(cancellationToken);
        outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
            externalSubjectId, invite.SiteId.Value, rolePermissions, now, idGenerator));

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

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
