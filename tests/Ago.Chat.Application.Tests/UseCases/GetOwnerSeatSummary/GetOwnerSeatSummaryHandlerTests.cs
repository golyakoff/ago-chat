using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOwnerSeatSummary;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetOwnerSeatSummary;

/// <summary>
/// `25-181`'s own read path - the owner console's "Пользователи" summary line, each role's own limit
/// already including the platform owner's live grant on top of whatever billing currently grants
/// (<c>Site.SeatLimit</c>/<c>Site.AdminLimit</c>).
///
/// <para><b>The item's own Done-when, proven with a fake clock, not asserted from reading the code:</b>
/// <see cref="HandleAsync_AGrantWithAnExpiry_RaisesTheLimit_ThenDropsBackOnceTheClockPassesIt"/> grants,
/// confirms the raised limit, advances the fake clock past the expiry, and confirms the limit drops
/// back - the exact three-step proof the backlog names.</para>
/// </summary>
public class GetOwnerSeatSummaryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string OperatorRoleName = "Operator";
    private const string AdminRoleName = "Admin";

    private sealed record Fixture(
        Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummaryHandler Handler, FakeOperatorRoleRepository OperatorRoles,
        FakeSiteRepository Sites, FakeOwnerSeatGrantStore Grants, FakeClock Clock);

    // `25-25`: the free tier's own AdminLimit default (SubscriptionTierBands.FreeAdminsIncluded) is 1 -
    // seeding via tier: "free" gets AdminLimit=1 straight from the constructor, the identical shortcut
    // AdministratorLimitEnforcerTests' own SeedSiteWithRolesAsync already takes, with no reflection
    // needed to reach a private setter.
    private static Fixture CreateFixture(int seatLimit = 2)
    {
        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: "free", seatLimit: seatLimit);
        sites.Seed(site);
        var grants = new FakeOwnerSeatGrantStore();
        var clock = new FakeClock(Now);

        var handler = new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummaryHandler(operatorRoles, sites, grants, clock);
        return new Fixture(handler, operatorRoles, sites, grants, clock);
    }

    [Fact]
    public async Task HandleAsync_NoOwnerGrant_ReportsTheBillingLimitAlone()
    {
        var fixture = CreateFixture(seatLimit: 2);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), AdminRoleName, holdsSeat: true);
        fixture.OperatorRoles.SeedSeat(new OperatorId(Guid.NewGuid()), OperatorRoleName, holdsSeat: true);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.OperatorsHeld);
        Assert.Equal(2, result.Value.OperatorsLimit);
        Assert.Equal(1, result.Value.AdministratorsHeld);
        Assert.Equal(1, result.Value.AdministratorsLimit);
    }

    [Fact]
    public async Task HandleAsync_SiteNotFound_ReturnsNotFound()
    {
        var operatorRoles = new FakeOperatorRoleRepository();
        var sites = new FakeSiteRepository();
        var handler = new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummaryHandler(
            operatorRoles, sites, new FakeOwnerSeatGrantStore(), new FakeClock(Now));

        var result = await handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    /// <summary>The item's own headline proof - see this class's own remarks.</summary>
    [Fact]
    public async Task HandleAsync_AGrantWithAnExpiry_RaisesTheLimit_ThenDropsBackOnceTheClockPassesIt()
    {
        var fixture = CreateFixture(seatLimit: 2);
        var expiresAt = Now.AddDays(30);
        await fixture.Grants.GrantAsync(
            SiteId, OwnerSeatGrantRole.Administrator, quantity: 1, "owner-sub", "incident cover", Now, expiresAt, CancellationToken.None);

        var beforeExpiry = await fixture.Handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);
        Assert.True(beforeExpiry.IsSuccess);
        Assert.Equal(2, beforeExpiry.Value.AdministratorsLimit); // 1 (billing) + 1 (owner grant)

        fixture.Clock.UtcNow = expiresAt.AddSeconds(1);

        var afterExpiry = await fixture.Handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);
        Assert.True(afterExpiry.IsSuccess);
        Assert.Equal(1, afterExpiry.Value.AdministratorsLimit); // back to billing alone
    }

    [Fact]
    public async Task HandleAsync_AnIndefiniteGrant_StaysGranted_FarIntoTheFuture()
    {
        var fixture = CreateFixture(seatLimit: 2);
        await fixture.Grants.GrantAsync(
            SiteId, OwnerSeatGrantRole.Administrator, quantity: 2, "owner-sub", "бессрочно", Now, expiresAt: null, CancellationToken.None);

        fixture.Clock.UtcNow = Now.AddYears(5);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.AdministratorsLimit); // 1 (billing) + 2 (owner grant)
    }

    [Fact]
    public async Task HandleAsync_AnOperatorGrant_RaisesTheOperatorLimit_NotTheAdministratorOne()
    {
        var fixture = CreateFixture(seatLimit: 2);
        await fixture.Grants.GrantAsync(
            SiteId, OwnerSeatGrantRole.Operator, quantity: 3, "owner-sub", "extra seats", Now, expiresAt: null, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummary(SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value.OperatorsLimit); // 2 (billing) + 3 (owner grant)
        Assert.Equal(1, result.Value.AdministratorsLimit); // untouched
    }
}
