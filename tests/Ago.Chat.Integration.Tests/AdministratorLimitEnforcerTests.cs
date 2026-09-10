using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-41`: the author's own decision, 2026-09-10, proven against real Postgres - when a site's
/// <see cref="Site.AdminLimit"/> drops, the operator(s) above the new ceiling are demoted back to
/// "Operator" automatically, most-recently-promoted-to-Administrator first
/// (<see cref="AdministratorLimitEnforcer"/>'s own remarks give the full tie-break reasoning, including
/// the deliberate treatment of an Administrator this table has no promotion record for at all - the
/// account's own founder, or an invite redeemed directly into "Admin").
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdministratorLimitEnforcerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private const string AdminRoleName = "Admin";
    private const string OperatorRoleName = "Operator";

    [Fact]
    public async Task DemoteExcessAdministratorsAsync_AmongTwoRealPromotions_DemotesTheMoreRecentOne()
    {
        var (siteId, adminRoleId, operatorRoleId) = await SeedSiteWithRolesAsync();
        var earlierPromoted = await SeedAdminAsync(siteId, adminRoleId);
        var laterPromoted = await SeedAdminAsync(siteId, adminRoleId);
        await RecordPromotionAsync(siteId, earlierPromoted, Now - TimeSpan.FromDays(10));
        await RecordPromotionAsync(siteId, laterPromoted, Now - TimeSpan.FromDays(1));

        await using (var db = fixture.CreateDbContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var enforcer = CreateEnforcer(db);
            await enforcer.DemoteExcessAdministratorsAsync(siteId, newAdminLimit: 1, Now, CancellationToken.None);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await AssertRoleAsync(laterPromoted, OperatorRoleName); // demoted - the more recent promotion
        await AssertRoleAsync(earlierPromoted, AdminRoleName); // stays - promoted first, so demoted last
    }

    /// <summary>`AdministratorLimitEnforcer`'s own deliberate rule for the case `role_change_records`
    /// cannot answer: an Administrator with no promotion record at all (the account's own founder, or
    /// an invite redeemed directly into "Admin") is demoted only after every Administrator this table
    /// does have a real record for.</summary>
    [Fact]
    public async Task DemoteExcessAdministratorsAsync_AnAdministratorWithNoPromotionRecord_IsDemotedLast()
    {
        var (siteId, adminRoleId, operatorRoleId) = await SeedSiteWithRolesAsync();
        var neverPromoted = await SeedAdminAsync(siteId, adminRoleId); // the "founder" - no role_change_records row
        var actuallyPromoted = await SeedAdminAsync(siteId, adminRoleId);
        await RecordPromotionAsync(siteId, actuallyPromoted, Now - TimeSpan.FromDays(1));

        await using (var db = fixture.CreateDbContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var enforcer = CreateEnforcer(db);
            await enforcer.DemoteExcessAdministratorsAsync(siteId, newAdminLimit: 1, Now, CancellationToken.None);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await AssertRoleAsync(actuallyPromoted, OperatorRoleName); // demoted - a real, recorded promotion
        await AssertRoleAsync(neverPromoted, AdminRoleName); // stays - never actually promoted, protected
    }

    [Fact]
    public async Task DemoteExcessAdministratorsAsync_WhenNotOverTheNewLimit_DoesNothing()
    {
        var (siteId, adminRoleId, operatorRoleId) = await SeedSiteWithRolesAsync();
        var admin = await SeedAdminAsync(siteId, adminRoleId);
        await RecordPromotionAsync(siteId, admin, Now - TimeSpan.FromDays(1));

        await using (var db = fixture.CreateDbContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var enforcer = CreateEnforcer(db);
            await enforcer.DemoteExcessAdministratorsAsync(siteId, newAdminLimit: 2, Now, CancellationToken.None);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await AssertRoleAsync(admin, AdminRoleName);
    }

    /// <summary>`25-41`'s own Done-when, end to end: a full lapse (the "charge itself lapsing" trigger)
    /// really does demote the excess Administrators, through the real trigger
    /// (<c>SubscriptionRenewalApplier.ApplyLapseAsync</c>), not only through a direct call to the
    /// enforcer in isolation.</summary>
    [Fact]
    public async Task ApplyLapseAsync_DemotesExcessAdministrators_ToTheFreeTiersOwnFloor()
    {
        var (siteId, adminRoleId, operatorRoleId) = await SeedSiteWithRolesAsync(tier: SubscriptionTierBands.Starter, seatLimit: 5);
        var subscriptionId = new BillingSubscriptionId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var subscription = BillingSubscription.Create(
                subscriptionId, siteId, $"pmt_{subscriptionId.Value:N}", 5, SubscriptionTierBands.Starter, 1, 1,
                Now - BillingSubscription.PeriodLength);
            // `MarkLapsed` allows a `Succeeded` row directly (`decisions/0006`'s own second trigger -
            // "a cancelled subscription reached its own paid-through period end with no charge ever
            // attempted") - no need to route this seed through PastDue first.
            subscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            db.BillingSubscriptions.Add(subscription);
            await db.SaveChangesAsync();
        }

        var earlierPromoted = await SeedAdminAsync(siteId, adminRoleId);
        var laterPromoted = await SeedAdminAsync(siteId, adminRoleId);
        await RecordPromotionAsync(siteId, earlierPromoted, Now - TimeSpan.FromDays(10));
        await RecordPromotionAsync(siteId, laterPromoted, Now - TimeSpan.FromDays(1));

        await using (var db = fixture.CreateDbContext())
        {
            var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
            var idGenerator = new UuidV7Generator();
            var applier = new SubscriptionRenewalApplier(
                db, outbox, idGenerator,
                new ModuleQuantityGrantStore(db, outbox, idGenerator),
                new NoOpBillingOptionEntitlementProvider(),
                CreateEnforcer(db));
            await applier.ApplyLapseAsync(subscriptionId, Now, CancellationToken.None);
        }

        var site = await fixture.CreateDbContext().Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, site.AdminLimit);
        // Free tier includes exactly one Administrator - the more recently promoted of the two is
        // demoted, leaving only the earlier promotion in place.
        await AssertRoleAsync(laterPromoted, OperatorRoleName);
        await AssertRoleAsync(earlierPromoted, AdminRoleName);
    }

    private async Task<(SiteId SiteId, Guid AdminRoleId, Guid OperatorRoleId)> SeedSiteWithRolesAsync(
        string tier = "free", int seatLimit = 1)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        var operatorRoleId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: tier, seatLimit: seatLimit));
        db.Roles.Add(new RoleRecord { Id = adminRoleId, SiteId = siteId, Name = AdminRoleName, Permissions = [Permission.SiteManageOperators.Value] });
        db.Roles.Add(new RoleRecord { Id = operatorRoleId, SiteId = siteId, Name = OperatorRoleName, Permissions = [Permission.ConversationAssign.Value] });
        await db.SaveChangesAsync(CancellationToken.None);

        return (siteId, adminRoleId, operatorRoleId);
    }

    private async Task<OperatorId> SeedAdminAsync(SiteId siteId, Guid adminRoleId)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = adminRoleId });
        await db.SaveChangesAsync(CancellationToken.None);
        return operatorId;
    }

    /// <summary>Seeds a real `role_change_records` row directly through the entity, the same "seed via
    /// EF, production writes however it writes" shortcut every other integration test in this project
    /// takes for a table with its own, separate production write path.</summary>
    private async Task RecordPromotionAsync(SiteId siteId, OperatorId operatorId, DateTimeOffset changedAt)
    {
        await using var db = fixture.CreateDbContext();
        db.RoleChangeRecords.Add(new RoleChangeRecordEntity
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            ChangedByOperatorId = null,
            ChangedOperatorId = operatorId,
            PreviousRoleNames = [OperatorRoleName],
            NewRoleName = AdminRoleName,
            ChangedAt = changedAt,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task AssertRoleAsync(OperatorId operatorId, string expectedRoleName)
    {
        await using var db = fixture.CreateDbContext();
        var roleName = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId)
            .Join(db.Roles, or => or.RoleId, r => r.Id, (or, r) => r.Name)
            .SingleAsync(CancellationToken.None);
        Assert.Equal(expectedRoleName, roleName);
    }

    private static AdministratorLimitEnforcer CreateEnforcer(AgoChatDbContext db)
    {
        var idGenerator = new UuidV7Generator();
        return new AdministratorLimitEnforcer(
            db, new OperatorRoleRepository(db), new RoleRepository(db, idGenerator, new SystemClockStub()),
            new RoleChangeRecordRepository(db), new EfOutboxWriter<AgoChatDbContext>(db), idGenerator);
    }

    /// <summary>A trivial <see cref="IClock"/> - <see cref="RoleRepository"/>'s own constructor needs
    /// one for <c>AddPermissionsAsync</c>, a method nothing in this file ever calls.</summary>
    private sealed class SystemClockStub : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>A trivial <see cref="IBillingOptionEntitlementProvider"/> - <see cref="SubscriptionRenewalApplier"/>'s
    /// own constructor needs one, but <see cref="ApplyLapseAsync_DemotesExcessAdministrators_ToTheFreeTiersOwnFloor"/>
    /// only ever exercises the base-row branch, which never reads it.</summary>
    private sealed class NoOpBillingOptionEntitlementProvider : IBillingOptionEntitlementProvider
    {
        public ModuleKey? TryGet(BillingOptionKey optionKey) => null;
    }
}
