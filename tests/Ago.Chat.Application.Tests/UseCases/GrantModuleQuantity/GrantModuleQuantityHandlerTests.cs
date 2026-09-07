using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GrantModuleQuantity;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GrantModuleQuantity;

/// <summary>`22-07`/`adr/0093`: the handler's own decisions, with every port faked - what the database
/// does inside a real transaction is `Ago.Chat.Integration.Tests`' job
/// (<c>ModuleQuantityGrantedOutboxTests</c>).</summary>
public class GrantModuleQuantityHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(
        GrantModuleQuantityHandler Handler, FakeModuleQuantityGrantStore Grants, FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool permitted = true)
    {
        var grants = new FakeModuleQuantityGrantStore();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        return new Fixture(new GrantModuleQuantityHandler(grants, permissions, new FakeClock(Now)), grants, permissions);
    }

    private static Application.UseCases.GrantModuleQuantity.GrantModuleQuantity Command(
        string moduleKey = "calendar", int quantity = 5) =>
        new(OperatorId, SiteId, moduleKey, quantity);

    [Fact]
    public async Task HandleAsync_WithNoConflict_GrantsTheQuantity()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 5), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, await fixture.Grants.GetQuantityAsync(SiteId, new ModuleKey("calendar"), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden_AndGrantsNothing()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
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
}

/// <summary>Records every call, so a test can assert exactly what was (or was not) granted - the same
/// hand-written-fake reasoning every other fake in this suite follows (testing.md).</summary>
public sealed class FakeModuleQuantityGrantStore : IModuleQuantityGrantStore
{
    public Dictionary<(SiteId, ModuleKey), int> Grants { get; } = [];

    public Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(Grants.GetValueOrDefault((siteId, moduleKey)));

    public Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
        SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<ModuleKey, int>>(
            Grants.Where(kv => kv.Key.Item1 == siteId).ToDictionary(kv => kv.Key.Item2, kv => kv.Value));

    public Task GrantAsync(
        SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Grants[(siteId, moduleKey)] = quantity;
        return Task.CompletedTask;
    }
}
