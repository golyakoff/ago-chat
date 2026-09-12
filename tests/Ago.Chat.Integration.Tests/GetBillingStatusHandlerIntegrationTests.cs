using Ago.Chat.Application.UseCases.GetBillingStatus;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-23`'s own backend Done-when: "a real integration test proves the extended DTO against a real
/// database state (an account with purchased extra Administrators, one without) - not just a
/// handler-level fake." <see cref="Application.Tests.UseCases.GetBillingStatus.GetBillingStatusHandlerTests"/>
/// already proves the handler's own branching against <see cref="Application.Tests.Fakes.FakeOperatorRoleRepository"/>/
/// <see cref="Application.Tests.Fakes.FakePriceCatalogRepository"/>; this file proves the same fields
/// survive a real round trip through <see cref="OperatorRoleRepository"/> and
/// <see cref="PriceCatalogRepository"/> against real Postgres - the identical "handler against real
/// repositories, no HTTP host" shape <see cref="AdministratorSlotChangeApplierTests"/> already
/// establishes for the analogous Administrator-purchase write path, applied here to this read.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GetBillingStatusHandlerIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_ForASiteThatPurchasedExtraAdministrators_ReportsTheRealSplitFromRealPostgres()
    {
        await SeedSeatPricesAsync();
        var adminExtraPriceRub = await SeedAdminExtraPriceAsync();

        var siteId = new SiteId(Guid.NewGuid());
        OperatorId adminOperatorId, secondAdminOperatorId;
        await using (var seed = fixture.CreateDbContext())
        {
            var site = new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5);
            seed.Sites.Add(site);

            var subscription = BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), siteId, $"pmt_{siteId.Value:N}", requestedSeats: 5,
                tier: SubscriptionTierBands.Starter, baseSeatPriceVersion: 1, extraSeatPriceVersion: 1, createdAt: Now - BillingSubscription.PeriodLength);
            subscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            subscription.ApplyAdministratorPurchase(newExtraAdministratorCount: 1, adminExtraPriceVersion: 1);
            seed.BillingSubscriptions.Add(subscription);

            (adminOperatorId, secondAdminOperatorId) = await SeedTwoAdministratorsAndOneOperatorAsync(seed, siteId);

            // `25-41`'s own Site.ActivateSubscription - the real write path that raises Site.AdminLimit
            // to reflect the purchase this test seeds. Called directly on the same tracked instance
            // just added above (not re-queried - nothing has been saved yet for a query to find), the
            // same "seed the consequence, not just the row" discipline AdministratorSlotChangeApplierTests
            // already uses, since this handler reads Site.AdminLimit, never re-derives it from the
            // subscription.
            site.ActivateSubscription(SubscriptionTierBands.Starter, 5, extraAdministrators: 1, Now);

            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var result = await CallHandlerAsync(siteId, adminOperatorId);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        var status = result.Value;
        Assert.Equal(SubscriptionTierBands.Starter, status.Tier);
        Assert.Equal("Business", status.TierDisplayName);
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded + 1, status.AdminLimit);
        // Two non-removed Administrators seeded (the caller plus one more) - one plain Operator seeded
        // alongside them must not be counted here, the real proof that AdminsUsed is scoped to the
        // "Admin" role and not merely "anyone with a seat".
        Assert.Equal(2, status.AdminsUsed);
        Assert.Equal(1, status.ExtraAdministratorsPurchased);
        Assert.Equal(adminExtraPriceRub, status.AdminExtraPriceRub);
        Assert.Equal(SeededBaseSeatPriceRub, status.SeatPricing.BaseSeatPriceRub);
        Assert.Equal(SeededExtraSeatPriceRub, status.SeatPricing.PricePerExtraSeatRub);
        Assert.Equal(SubscriptionTierBands.MinSeats, status.SeatPricing.MinSeats);
        Assert.Equal(SubscriptionTierBands.MaxSeats, status.SeatPricing.MaxSeats);

        // Every second operator holds a seat too, but only the Administrator-role holders above are
        // what AdminsUsed counts - Operator seats stay this handler's own pre-existing SeatsUsed field.
        Assert.Equal(3, status.SeatsUsed);
    }

    [Fact]
    public async Task HandleAsync_ForASiteThatNeverPurchasedAnExtraAdministrator_ReportsZero_NotAFabricatedNumber()
    {
        await SeedSeatPricesAsync();

        var siteId = new SiteId(Guid.NewGuid());
        OperatorId adminOperatorId;
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: "free", seatLimit: 1));
            adminOperatorId = await SeedOneAdministratorAsync(seed, siteId);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var result = await CallHandlerAsync(siteId, adminOperatorId);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.Message : null);
        var status = result.Value;
        Assert.Equal("free", status.Tier);
        Assert.Equal("Solo", status.TierDisplayName);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, status.AdminLimit);
        Assert.Equal(1, status.AdminsUsed);
        Assert.Equal(0, status.ExtraAdministratorsPurchased);
        // `25-43`'s own second decision ("no published version" reads back as a real, honest null,
        // never a fabricated zero) is proven deterministically at the Application-level fake test
        // (GetBillingStatusHandlerTests.HandleAsync_WhenSiteHasNeverCheckedOut_...), not repeated here:
        // AdminExtraPriceKey is one global key shared by every test in this file's own PostgresCollection
        // container (the sibling test above may publish a version for it before or after this one runs,
        // in either test-execution order), so "AdminExtraPriceRub is null" is not a fact this specific
        // site's own database state can prove in isolation the way ExtraAdministratorsPurchased below
        // can - asserting it here would make this test's outcome depend on inter-test ordering rather
        // than on anything this site actually did.
        Assert.Null(status.LatestSubscription);
    }

    private async Task<Result<BillingStatusDto>> CallHandlerAsync(SiteId siteId, OperatorId requestedBy)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new GetBillingStatusHandler(
            new SiteRepository(db), new OperatorRepository(db), new BillingSubscriptionRepository(db),
            new OperatorRoleRepository(db), new PriceCatalogRepository(db), new PermissionChecker(db));

        return await handler.HandleAsync(new GetBillingStatus(requestedBy, siteId), CancellationToken.None);
    }

    private const decimal SeededBaseSeatPriceRub = 490m;

    private const decimal SeededExtraSeatPriceRub = 200m;

    /// <summary>Publishes fresh versions of both seat-pricing keys - the identical "get the resource
    /// if it already exists, publish a new version regardless" discipline
    /// <c>OwnerPricingEndpointTests.SeedSeatPricesAsync</c> already establishes for the same two keys
    /// sharing this same <see cref="PostgresCollection"/> container, restated here since this file
    /// builds its own handler from scratch rather than sharing that file's test host.</summary>
    private async Task SeedSeatPricesAsync()
    {
        await using var db = fixture.CreateDbContext();
        var prices = new PriceCatalogRepository(db);

        var baseResource = await prices.GetByKeyAsync(SubscriptionTierBands.BaseSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.BaseSeatPriceKey);
        baseResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededBaseSeatPriceRub, Now);
        await prices.SaveAsync(baseResource, CancellationToken.None);

        var extraResource = await prices.GetByKeyAsync(SubscriptionTierBands.ExtraSeatPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.ExtraSeatPriceKey);
        extraResource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), SeededExtraSeatPriceRub, Now);
        await prices.SaveAsync(extraResource, CancellationToken.None);
    }

    private async Task<decimal> SeedAdminExtraPriceAsync()
    {
        const decimal amountRub = 500m;
        await using var db = fixture.CreateDbContext();
        var prices = new PriceCatalogRepository(db);

        var resource = await prices.GetByKeyAsync(SubscriptionTierBands.AdminExtraPriceKey, CancellationToken.None)
            ?? PricedResource.Create(new PricedResourceId(Guid.NewGuid()), SubscriptionTierBands.AdminExtraPriceKey);
        resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), amountRub, Now);
        await prices.SaveAsync(resource, CancellationToken.None);
        return amountRub;
    }

    /// <summary>Two non-removed "Admin" role holders (one of them the caller, so the same permission
    /// check the handler's own forbidden-path test already covers for this file's collaborators is
    /// satisfied) plus one plain "Operator" seat holder - the row AdminsUsed must not count.</summary>
    private static async Task<(OperatorId First, OperatorId Second)> SeedTwoAdministratorsAndOneOperatorAsync(AgoChatDbContext db, SiteId siteId)
    {
        var first = await SeedRoleHolderAsync(db, siteId, "Admin", [Permission.SiteConfigure.Value]);
        var second = await SeedRoleHolderAsync(db, siteId, "Admin", [Permission.SiteConfigure.Value]);
        await SeedRoleHolderAsync(db, siteId, "Operator", []);
        return (first, second);
    }

    private static async Task<OperatorId> SeedOneAdministratorAsync(AgoChatDbContext db, SiteId siteId) =>
        await SeedRoleHolderAsync(db, siteId, "Admin", [Permission.SiteConfigure.Value]);

    private static async Task<OperatorId> SeedRoleHolderAsync(AgoChatDbContext db, SiteId siteId, string roleName, List<string> permissions)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: $"sub_{operatorId.Value:N}"));
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = roleName, Permissions = permissions });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
        return operatorId;
    }
}
