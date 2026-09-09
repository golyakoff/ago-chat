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

    private sealed record Fixture(
        Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler Handler,
        FakeModuleQuantityGrantStore Grants);

    private static Fixture CreateFixture()
    {
        var grants = new FakeModuleQuantityGrantStore();
        return new Fixture(
            new Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwnerHandler(grants, new FakeClock(Now)),
            grants);
    }

    private static Application.UseCases.GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwner Command(
        string moduleKey = "calendar", int quantity = 5) =>
        new(SiteId, moduleKey, quantity);

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
}
