using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GrantOwnerSeatsAsOwner;

/// <summary>`25-181`'s own write path - modeled on
/// <see cref="ModuleQuantityGrant.SetUnconditionalGrant"/>'s validated shape, proven here the same way
/// <see cref="Application.UseCases.RestoreOperatorSeatAsOwner.RestoreOperatorSeatAsOwnerHandlerTests"/>'s
/// own force/reason tests prove the sibling override: a blank/overlong reason and an out-of-range
/// quantity are refused before the store is ever touched.</summary>
public class GrantOwnerSeatsAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private const string PlatformOwnerSubject = "keycloak-sub-of-the-platform-owner";
    private const string ValidReason = "Covering an incident while the tenant sorts out billing.";

    private sealed record Fixture(Application.UseCases.GrantOwnerSeatsAsOwner.GrantOwnerSeatsAsOwnerHandler Handler, FakeOwnerSeatGrantStore Grants, FakeSiteRepository Sites);

    private static Fixture CreateFixture(bool seedSite = true)
    {
        var grants = new FakeOwnerSeatGrantStore();
        var sites = new FakeSiteRepository();
        if (seedSite)
        {
            sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 3));
        }

        var handler = new Application.UseCases.GrantOwnerSeatsAsOwner.GrantOwnerSeatsAsOwnerHandler(grants, sites, new FakeClock(Now));
        return new Fixture(handler, grants, sites);
    }

    private static Application.UseCases.GrantOwnerSeatsAsOwner.GrantOwnerSeatsAsOwner Command(
        OwnerSeatGrantRole role = OwnerSeatGrantRole.Administrator, int quantity = 1, string reason = ValidReason,
        DateTimeOffset? expiresAt = null) =>
        new(SiteId, role, quantity, PlatformOwnerSubject, reason, expiresAt);

    [Fact]
    public async Task HandleAsync_AValidGrant_Succeeds_AndWritesTheGrant()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(OwnerSeatGrantRole.Administrator, quantity: 2), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var grant = fixture.Grants.Get(SiteId, OwnerSeatGrantRole.Administrator);
        Assert.NotNull(grant);
        Assert.Equal(2, grant!.Quantity);
        Assert.Equal(PlatformOwnerSubject, grant.GrantedBy);
        Assert.Equal(ValidReason, grant.Reason);
        Assert.Equal(Now, grant.GrantedAt);
        Assert.Null(grant.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_WithAnExpiry_RecordsIt()
    {
        var fixture = CreateFixture();
        var expiresAt = Now.AddDays(30);

        var result = await fixture.Handler.HandleAsync(Command(expiresAt: expiresAt), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(expiresAt, fixture.Grants.Get(SiteId, OwnerSeatGrantRole.Administrator)!.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_NoReason_IsRefused_BeforeTouchingTheStore()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(reason: null!), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.OwnerSeatGrantReasonRequired", result.Error!.Value.Code);
        Assert.Null(fixture.Grants.Get(SiteId, OwnerSeatGrantRole.Administrator));
    }

    [Fact]
    public async Task HandleAsync_ABlankReason_IsRefused_TheSameAsNoReasonAtAll()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(reason: "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.OwnerSeatGrantReasonRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_AnOverlongReason_IsRefused()
    {
        var fixture = CreateFixture();
        var overlong = new string('x', Application.UseCases.GrantOwnerSeatsAsOwner.GrantOwnerSeatsAsOwnerHandler.MaxReasonLength + 1);

        var result = await fixture.Handler.HandleAsync(Command(reason: overlong), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.OwnerSeatGrantReasonRequired", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task HandleAsync_AQuantityOutsideOneToFive_IsRefused_BeforeTouchingTheStore(int quantity)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: quantity), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.OwnerSeatGrantQuantityInvalid", result.Error!.Value.Code);
        Assert.Null(fixture.Grants.Get(SiteId, OwnerSeatGrantRole.Administrator));
    }

    [Fact]
    public async Task HandleAsync_SiteNotFound_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedSite: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`25-181`'s own snapshot posture, restated for the write path: re-granting the same role
    /// replaces the prior fact outright, never accumulates - the identical
    /// <see cref="ModuleQuantityGrant.SetQuantity"/> discipline this type's own remarks reuse.</summary>
    [Fact]
    public async Task HandleAsync_RegrantingTheSameRole_ReplacesThePriorGrant_NeverAccumulates()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(OwnerSeatGrantRole.Operator, quantity: 5, reason: "first"), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            Command(OwnerSeatGrantRole.Operator, quantity: 1, reason: "corrected"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var grant = fixture.Grants.Get(SiteId, OwnerSeatGrantRole.Operator);
        Assert.Equal(1, grant!.Quantity);
        Assert.Equal("corrected", grant.Reason);
    }
}
