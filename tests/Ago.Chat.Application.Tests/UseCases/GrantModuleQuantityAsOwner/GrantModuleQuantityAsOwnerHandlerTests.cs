using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.Tests.UseCases.GrantModuleQuantity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GrantModuleQuantityAsOwner;

/// <summary>`23-66`: the platform owner's own caller of <see cref="Application.Abstractions.IModuleQuantityGrantStore"/> -
/// every port faked, exactly like its tenant-facing sibling's own tests
/// (<c>GrantModuleQuantityHandlerTests</c>), minus a permission check this handler deliberately never
/// makes (this type's own remarks). What a real Postgres transaction does is
/// <c>Ago.Chat.Integration.Tests.OwnerModuleEndpointsTests</c>' job.</summary>
public class GrantModuleQuantityAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey CalendarModuleKey = new("calendar");

    private sealed record Fixture(
        Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler Handler,
        FakeModuleQuantityGrantStore Grants,
        FakeModuleQuantityImpactPreviewStore Previews);

    private static Fixture CreateFixture()
    {
        var grants = new FakeModuleQuantityGrantStore();
        var previews = new FakeModuleQuantityImpactPreviewStore();
        return new Fixture(
            new Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler(grants, previews, new FakeClock(Now)),
            grants,
            previews);
    }

    private static Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwner Command(
        string moduleKey = "calendar", int quantity = 5, int? expectedAffectedCount = null) =>
        new(SiteId, moduleKey, quantity, expectedAffectedCount);

    [Fact]
    public async Task HandleAsync_WithNoRequesterAtAll_StillGrants()
    {
        // The headline distinction from the tenant-facing sibling: no OperatorId on the command, no
        // IPermissionChecker call - RequirePlatformOwner at the route is the whole authorization
        // story (this handler's own remarks).
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, await fixture.Grants.GetQuantityAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithAZeroQuantity_Succeeds()
    {
        // `23-66`'s own warning: zero is a legitimate grant, not refused as though it meant nothing.
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 0), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, await fixture.Grants.GetQuantityAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Calendar")]
    [InlineData("has spaces")]
    public async Task HandleAsync_WithAnInvalidModuleKey_IsRejected(string moduleKey)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(moduleKey: moduleKey), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }

    [Fact]
    public async Task HandleAsync_WithANegativeQuantity_IsRejected()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: -1), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceWithADifferentQuantity_OverwritesTheEarlierOne()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(quantity: 5), CancellationToken.None);

        await fixture.Handler.HandleAsync(Command(quantity: 2), CancellationToken.None);

        Assert.Equal(2, await fixture.Grants.GetQuantityAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
        Assert.Single(fixture.Grants.Grants);
    }

    /// <summary>`23-89`: the platform owner's own version of the identical worry - a grant made
    /// during a support call, repeated because nothing showed the tenant catching up yet
    /// (<see cref="Application.UseCases.GrantModuleQuantity.GrantModuleQuantityHandlerTests"/>'s own
    /// sibling test states the full reasoning). This handler never checks permission, but the store
    /// underneath is the identical snapshot write either caller reaches.</summary>
    [Fact]
    public async Task HandleAsync_CalledTwiceWithTheSameQuantity_IsANoOp()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(quantity: 5), CancellationToken.None);

        var second = await fixture.Handler.HandleAsync(Command(quantity: 5), CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(5, await fixture.Grants.GetQuantityAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
        Assert.Single(fixture.Grants.Grants);
    }

    /// <summary>`23-88`: no <c>ExpectedAffectedCount</c> preserves this command's own original,
    /// unconditional behaviour - identical reasoning to the tenant-facing sibling's own test.</summary>
    [Fact]
    public async Task HandleAsync_WithNoExpectedAffectedCount_GrantsUnconditionally()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await fixture.Grants.GetQuantityAsync(SiteId, CalendarModuleKey, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithExpectedAffectedCountMatchingTheAnsweredPreview_Grants()
    {
        var fixture = CreateFixture();
        await fixture.Previews.RequestAsync(SiteId, CalendarModuleKey, 2, Now, CancellationToken.None);
        await fixture.Previews.AnswerAsync(SiteId, CalendarModuleKey, 2, 3, new[] { "Anna", "Boris", "Vera" }, Now, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: 3), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await fixture.Grants.GetQuantityAsync(SiteId, CalendarModuleKey, CancellationToken.None));
    }

    /// <summary>`23-88`'s own fails-before, on the owner-facing route: the answer said 3, the owner
    /// confirms against 2 - refused, nothing granted.</summary>
    [Fact]
    public async Task HandleAsync_WithExpectedAffectedCountDisagreeingWithTheAnsweredPreview_Refuses_AndGrantsNothing()
    {
        var fixture = CreateFixture();
        await fixture.Previews.RequestAsync(SiteId, CalendarModuleKey, 2, Now, CancellationToken.None);
        await fixture.Previews.AnswerAsync(SiteId, CalendarModuleKey, 2, 3, new[] { "Anna", "Boris", "Vera" }, Now, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: 2), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityImpactStale", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }

    [Fact]
    public async Task HandleAsync_WithExpectedAffectedCountButNoPreviewEverRequested_Refuses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityImpactStale", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }
}
