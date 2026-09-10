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
/// `25-41`: <see cref="IAdministratorLimitEnforcer"/>'s own implementation - the author's own decision,
/// 2026-09-10, made real: when a site's <see cref="Site.AdminLimit"/> drops below its live count of
/// non-removed Administrators, the excess are demoted back to the seeded <c>"Operator"</c> role,
/// automatically, in the same transaction as whatever lapse or downgrade caused the drop.
///
/// <para><b>The tie-break, the one open design decision this item's own backlog left for whoever built
/// it.</b> `adr/0125`'s own precedent (<c>WorkerQuotaPolicy</c>, `ago-calendar`) is "most-recently-
/// granted first" - applied here as "most-recently-promoted-to-Administrator first". The backlog's own
/// suggestion assumed a ready-made ordering/timestamp field might not exist; it does, but only
/// partially: <c>role_change_records</c> (`23-72`) carries <c>changed_at</c> for every promotion made
/// through <c>ChangeOperatorRoleHandler</c>, keyed by <c>changed_operator_id</c> and
/// <c>new_role_name = "Admin"</c> - this is the real ordering field, read here for the first time
/// (<see cref="RoleChangeRecordEntity"/>'s own remarks on why that DbSet's "nothing ever queries this"
/// claim is no longer true).</para>
///
/// <para><b>What that table does not cover, and the deliberate choice made for it.</b> Two paths put an
/// operator directly into the <c>"Admin"</c> role without ever calling
/// <c>ChangeOperatorRoleHandler</c> - the account's own founder, seeded both roles at registration
/// (<c>SiteRegistrationRepository</c>), and an invite redeemed directly into <c>"Admin"</c>
/// (<c>OperatorInviteRedemptionRepository</c>). Neither writes a <c>role_change_records</c> row, because
/// neither is a <em>promotion</em> in the sense that table records - a founder or an invited
/// Administrator was never anything else first. Rather than inventing a synthetic timestamp for them
/// (their own <c>operators.created_at</c> would answer a different question - "when did this person
/// join", not "when did they become Administrator", and conflating the two would misrank a long-tenured
/// Operator who was promoted to Admin yesterday against a founder who has been Admin since day one) or
/// treating the absence as an error, this ranks every Administrator with a real promotion record ahead
/// of every one without one, most-recent-promotion-first within that group - an Administrator who was
/// literally never promoted (founder, invite-direct) is demoted only after every promoted Administrator
/// already has been. Ties within either group (a same-instant race, or two operators with no record at
/// all) fall back to <see cref="OperatorId"/> ordering - arbitrary, but a total order is still needed to
/// make the demotion count exact, and no better signal exists for that residual case.</para>
///
/// <para><b>Locks the site row before counting</b> - the identical single-row mutex
/// <see cref="IOperatorRoleRepository.CountNonRemovedHoldersAsync"/> and <c>ChangeOperatorRoleHandler</c>'s
/// own promotion guard already take before counting Administrators, for the identical reason: this is a
/// count-then-act decision, and a concurrent promotion landing between the count and the demotion could
/// otherwise demote one Administrator too many or too few. Rare and low-contention (this only ever runs
/// from a background renewal job or a webhook, never a hot path), so one lock is the simplest correct
/// choice, the same judgement that method's own remarks already make.</para>
///
/// <para><b>No "last administrator" guard, unlike <see cref="Domain.Operator.Remove"/>'s own invariant.</b>
/// <see cref="SubscriptionTierBands.ResolveAdminLimit"/> never returns less than <c>1</c>
/// (<see cref="SubscriptionTierBands.FreeAdminsIncluded"/> is the floor even on the free tier), so
/// <paramref name="newAdminLimit"/> can never ask this method to demote every Administrator down to
/// zero - the invariant "at least one Administrator remains" holds as a consequence of that floor, not
/// because this method checks for it.</para>
/// </summary>
public sealed class AdministratorLimitEnforcer(
    AgoChatDbContext db,
    IOperatorRoleRepository operatorRoles,
    IRoleRepository roles,
    IRoleChangeRecordRepository roleChangeRecords,
    IOutboxWriter outbox,
    IIdGenerator idGenerator)
    : IAdministratorLimitEnforcer
{
    private const string AdminRoleName = "Admin";

    private const string OperatorRoleName = "Operator";

    public async Task DemoteExcessAdministratorsAsync(
        SiteId siteId, int newAdminLimit, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockSiteAsync(siteId, cancellationToken);

        var holderIds = await operatorRoles.GetNonRemovedHolderIdsAsync(siteId, AdminRoleName, cancellationToken);
        var excessCount = holderIds.Count - newAdminLimit;
        if (excessCount <= 0)
        {
            return;
        }

        // The real ordering field - see this type's own remarks for why it only partially covers
        // today's Administrators, and the deliberate rule for the ones it does not.
        var promotedAt = await db.RoleChangeRecords
            .Where(r => r.SiteId == siteId && r.NewRoleName == AdminRoleName)
            .GroupBy(r => r.ChangedOperatorId)
            .Select(g => new { OperatorId = g.Key, PromotedAt = g.Max(r => r.ChangedAt) })
            .ToDictionaryAsync(x => x.OperatorId, x => x.PromotedAt, cancellationToken);

        var toDemote = holderIds
            .OrderByDescending(id => promotedAt.ContainsKey(id))
            .ThenByDescending(id => promotedAt.GetValueOrDefault(id))
            .ThenBy(id => id.Value)
            .Take(excessCount)
            .ToList();

        var operatorRole = await roles.GetByNameAsync(siteId, OperatorRoleName, cancellationToken);
        if (operatorRole is null)
        {
            // The seeded "Operator" role should exist for every site (SiteRegistrationRepository's own
            // seeding) - the same "should have prevented this" throw ChangeOperatorRoleHandler's own
            // missing-site guard raises for the analogous impossible case.
            throw new InvalidOperationException(
                $"Site {siteId.Value} has no role named '{OperatorRoleName}' while demoting excess Administrators - " +
                "SiteRegistrationRepository's own seeding should have prevented this.");
        }

        foreach (var operatorId in toDemote)
        {
            var previousRoleNames = await operatorRoles.GetRoleNamesAsync(operatorId, cancellationToken);
            await operatorRoles.ReplaceRoleAsync(operatorId, operatorRole.Id, cancellationToken);
            await roleChangeRecords.RecordAsync(
                new RoleChangeRecordToWrite(
                    idGenerator.NewId(now), siteId, ChangedByOperatorId: null, operatorId, previousRoleNames, OperatorRoleName, now),
                cancellationToken);

            // `22-05`/`adr/0093`: the demoted operator's newly resolved permission set - the identical
            // fact ChangeOperatorRoleHandler's own equivalent write already publishes, so the
            // cross-product role-assignment projection never lags behind an automatic demotion any more
            // than it would a human-requested one.
            var externalSubjectId = await db.Operators
                .Where(o => o.Id == operatorId)
                .Select(o => o.ExternalSubjectId)
                .FirstOrDefaultAsync(cancellationToken);
            if (externalSubjectId is not null)
            {
                outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
                    externalSubjectId, siteId.Value, operatorRole.Permissions, now, idGenerator));
            }
        }
    }

    private async Task LockSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using var command = new NpgsqlCommand("SELECT id FROM sites WHERE id = @siteId FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException(
                $"Site {siteId.Value} was not found while demoting excess Administrators - a foreign key should have prevented this.");
        }
    }
}
