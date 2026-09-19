using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-170`: the author's own decision - when a role's own live held-seat count exceeds its `Site`
/// limit, the excess is disabled (`HoldsSeat = false`), most-recently-granted first, proven against real
/// Postgres. Replaces `AdministratorLimitEnforcerTests` (`25-41`'s own now-retired enforcer) - the
/// identical tie-break reasoning that class's own remarks gave for `role_change_records.changed_at`
/// applies here to `operator_roles.granted_at` directly, and this item's own design applies the
/// identical procedure to both seeded roles, not the Admin role alone.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperatorRoleSeatReconcilerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private const string AdminRoleName = "Admin";
    private const string OperatorRoleName = "Operator";

    [Fact]
    public async Task ReconcileAsync_AmongTwoGrantedSeats_DisablesTheMoreRecentlyGrantedOne()
    {
        var (siteId, adminRoleId, _) = await SeedSiteWithRolesAsync();
        var earlierGranted = await SeedAdminAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedAdminAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));

        var disabledCount = await ReconcileAsync(siteId, tier: "free", seatLimit: 1, extraAdministrators: 0, AdminRoleName);

        Assert.Equal(1, disabledCount);
        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: false); // disabled - the more recent grant
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true); // stays - granted first, so disabled last
    }

    [Fact]
    public async Task ReconcileAsync_WhenNotOverTheLimit_DoesNothing()
    {
        var (siteId, adminRoleId, _) = await SeedSiteWithRolesAsync();
        var admin = await SeedAdminAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));

        var disabledCount = await ReconcileAsync(siteId, tier: "free", seatLimit: 1, extraAdministrators: 1, AdminRoleName);

        Assert.Equal(0, disabledCount);
        await AssertHoldsSeatAsync(admin, adminRoleId, expected: true);
    }

    /// <summary>This item's own design: "applied to the Operator role and the Admin role identically" -
    /// proven by running the identical procedure against the seeded Operator role too, not merely
    /// asserted from the parameterization looking symmetrical.</summary>
    [Fact]
    public async Task ReconcileAsync_AppliesIdenticallyToTheOperatorRole()
    {
        var (siteId, _, operatorRoleId) = await SeedSiteWithRolesAsync(seatLimit: 1);
        var earlierGranted = await SeedOperatorRoleHolderAsync(siteId, operatorRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedOperatorRoleHolderAsync(siteId, operatorRoleId, Now - TimeSpan.FromDays(1));

        var disabledCount = await ReconcileAsync(siteId, tier: "free", seatLimit: 1, extraAdministrators: 0, OperatorRoleName);

        Assert.Equal(1, disabledCount);
        await AssertHoldsSeatAsync(laterGranted, operatorRoleId, expected: false);
        await AssertHoldsSeatAsync(earlierGranted, operatorRoleId, expected: true);
    }

    /// <summary>`25-170`'s own Done-when, end to end: a full lapse (the "charge itself lapsing" trigger)
    /// really does disable the excess Administrators, through the real trigger
    /// (<c>SubscriptionRenewalApplier.ApplyLapseAsync</c>), not only through a direct call to the
    /// reconciler in isolation.</summary>
    [Fact]
    public async Task ApplyLapseAsync_DisablesExcessAdministrators_ToTheFreeTiersOwnFloor()
    {
        var (siteId, adminRoleId, _) = await SeedSiteWithRolesAsync(tier: SubscriptionTierBands.Starter, seatLimit: 5);
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

        var earlierGranted = await SeedAdminAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedAdminAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));

        await using (var db = fixture.CreateDbContext())
        {
            var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
            var idGenerator = new UuidV7Generator();
            var applier = new SubscriptionRenewalApplier(
                db, outbox, idGenerator,
                new ModuleQuantityGrantStore(db, outbox, idGenerator, new Ago.Platform.Hosting.SystemClock()),
                new NoOpBillingOptionEntitlementProvider(),
                new OperatorRoleSeatReconciler(new OperatorRoleRepository(db)));
            await applier.ApplyLapseAsync(subscriptionId, Now, CancellationToken.None);
        }

        var site = await fixture.CreateDbContext().Sites.SingleAsync(s => s.Id == siteId);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, site.AdminLimit);
        // Free tier includes exactly one Administrator - the more recently granted of the two is
        // disabled, leaving only the earlier grant holding its own seat.
        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: false);
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true);
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

    private async Task<OperatorId> SeedAdminAsync(SiteId siteId, Guid adminRoleId, DateTimeOffset grantedAt)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = adminRoleId, HoldsSeat = true, GrantedAt = grantedAt });
        await db.SaveChangesAsync(CancellationToken.None);
        return operatorId;
    }

    private async Task<OperatorId> SeedOperatorRoleHolderAsync(SiteId siteId, Guid operatorRoleId, DateTimeOffset grantedAt)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = operatorRoleId, HoldsSeat = true, GrantedAt = grantedAt });
        await db.SaveChangesAsync(CancellationToken.None);
        return operatorId;
    }

    /// <summary>Loads the site fresh, opens the ambient transaction <see cref="OperatorRoleSeatReconciler.ReconcileAsync"/>'s
    /// own row lock requires, and runs it for <paramref name="roleName"/> - the identical
    /// "reload fresh inside a real transaction" shape every enforcer/reconciler test in this project
    /// already takes.</summary>
    private async Task<int> ReconcileAsync(
        SiteId siteId, string tier, int seatLimit, int extraAdministrators, string roleName)
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var site = await db.Sites.SingleAsync(s => s.Id == siteId);
        // Matches AdminLimit to whatever this test wants to exercise, the identical
        // `Entry(...).Property(...).CurrentValue` shape this project's own concurrency tests already use
        // to simulate a private-setter write without going through ActivateSubscription's own
        // tier-derivation formula.
        db.Entry(site).Property(nameof(Site.AdminLimit)).CurrentValue =
            SubscriptionTierBands.ResolveAdminLimit(tier) + extraAdministrators;
        db.Entry(site).Property(nameof(Site.SeatLimit)).CurrentValue = seatLimit;

        var reconciler = new OperatorRoleSeatReconciler(new OperatorRoleRepository(db));
        var disabledCount = await reconciler.ReconcileAsync(site, roleName, CancellationToken.None);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return disabledCount;
    }

    private async Task AssertHoldsSeatAsync(OperatorId operatorId, Guid roleId, bool expected)
    {
        await using var db = fixture.CreateDbContext();
        var holdsSeat = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId && or.RoleId == roleId)
            .Select(or => or.HoldsSeat)
            .SingleAsync(CancellationToken.None);
        Assert.Equal(expected, holdsSeat);
    }

    /// <summary>A trivial <see cref="IBillingOptionEntitlementProvider"/> - <see cref="SubscriptionRenewalApplier"/>'s
    /// own constructor needs one, but <see cref="ApplyLapseAsync_DisablesExcessAdministrators_ToTheFreeTiersOwnFloor"/>
    /// only ever exercises the base-row branch, which never reads it.</summary>
    private sealed class NoOpBillingOptionEntitlementProvider : IBillingOptionEntitlementProvider
    {
        public ModuleKey? TryGet(BillingOptionKey optionKey) => null;
    }
}
