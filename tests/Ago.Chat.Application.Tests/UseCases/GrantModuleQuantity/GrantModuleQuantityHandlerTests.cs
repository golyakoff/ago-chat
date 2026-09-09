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
    private static readonly ModuleKey CalendarModuleKey = new("calendar");

    private sealed record Fixture(
        GrantModuleQuantityHandler Handler,
        FakeModuleQuantityGrantStore Grants,
        FakeModuleQuantityImpactPreviewStore Previews,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool permitted = true)
    {
        var grants = new FakeModuleQuantityGrantStore();
        var previews = new FakeModuleQuantityImpactPreviewStore();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        return new Fixture(new GrantModuleQuantityHandler(grants, previews, permissions, new FakeClock(Now)), grants, previews, permissions);
    }

    private static Application.UseCases.GrantModuleQuantity.GrantModuleQuantity Command(
        string moduleKey = "calendar", int quantity = 5, int? expectedAffectedCount = null) =>
        new(OperatorId, SiteId, moduleKey, quantity, expectedAffectedCount);

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

    /// <summary>
    /// `23-89`: the failure a support call makes cheap to imagine - an owner grants, sees no
    /// immediate change (the module applies asynchronously off the outbox), and grants the identical
    /// quantity again rather than waiting. Nothing about a second identical call may double anything:
    /// two calls land on the same stored quantity, not two grants layered on top of each other - the
    /// same snapshot reasoning <see cref="ModuleQuantityGrant"/>'s own remarks give for why a
    /// redelivered event is safe on the receiving end, proven here on the sending side instead.
    /// </summary>
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

    /// <summary>`23-88`: no <c>ExpectedAffectedCount</c> at all preserves this command's own original,
    /// unconditional behaviour exactly - a caller that never went through the async preview round
    /// trip is not newly blocked by a check it never asked for.</summary>
    [Fact]
    public async Task HandleAsync_WithNoExpectedAffectedCount_GrantsUnconditionally_EvenWithNoPreviewEverRequested()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await fixture.Grants.GetQuantityAsync(SiteId, CalendarModuleKey, CancellationToken.None));
    }

    /// <summary>`23-88`'s own real proof: an owner who previewed candidate 2, was told it affects 3,
    /// and confirms against exactly that - the write applies.</summary>
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

    /// <summary>`23-88`'s own fails-before: the answered preview said 3, the owner is confirming
    /// against 2 (a newer answer arrived, or they are simply wrong) - refused, and nothing is
    /// granted.</summary>
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

    /// <summary>The preview was requested for candidate 2 but the owner is confirming quantity 3 - a
    /// number that was never actually previewed at all, regardless of what the stored answer says.</summary>
    [Fact]
    public async Task HandleAsync_WithExpectedAffectedCountForADifferentCandidateQuantity_Refuses()
    {
        var fixture = CreateFixture();
        await fixture.Previews.RequestAsync(SiteId, CalendarModuleKey, 2, Now, CancellationToken.None);
        await fixture.Previews.AnswerAsync(SiteId, CalendarModuleKey, 2, 3, Array.Empty<string>(), Now, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Command(quantity: 3, expectedAffectedCount: 3), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityImpactStale", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }

    /// <summary>The preview was requested but the module never answered - <c>AnsweredAt</c> is still
    /// null. Confirming against any count at all is refused; there is nothing yet to have confirmed
    /// against.</summary>
    [Fact]
    public async Task HandleAsync_WithExpectedAffectedCountButThePreviewWasNeverAnswered_Refuses()
    {
        var fixture = CreateFixture();
        await fixture.Previews.RequestAsync(SiteId, CalendarModuleKey, 2, Now, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Command(quantity: 2, expectedAffectedCount: 0), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityImpactStale", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.Grants);
    }

    /// <summary>No preview was ever requested for this site/module at all - confirming against any
    /// expected count is refused, the same as an unanswered one.</summary>
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

/// <summary>`23-88`: the identical hand-written-fake shape <see cref="FakeModuleQuantityGrantStore"/>
/// establishes for its own sibling port, over <see cref="ModuleQuantityImpactPreview"/> instead of
/// <see cref="ModuleQuantityGrant"/>.</summary>
public sealed class FakeModuleQuantityImpactPreviewStore : IModuleQuantityImpactPreviewStore
{
    public Dictionary<(SiteId, ModuleKey), ModuleQuantityImpactPreview> Previews { get; } = [];

    public Task<ModuleQuantityImpactPreview?> TryGetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
        Task.FromResult(Previews.GetValueOrDefault((siteId, moduleKey)));

    public Task RequestAsync(
        SiteId siteId, ModuleKey moduleKey, int requestedQuantity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Previews.TryGetValue((siteId, moduleKey), out var existing))
        {
            existing.Reset(requestedQuantity, now);
        }
        else
        {
            Previews[(siteId, moduleKey)] = ModuleQuantityImpactPreview.Request(siteId, moduleKey, requestedQuantity, now);
        }

        return Task.CompletedTask;
    }

    public Task AnswerAsync(
        SiteId siteId, ModuleKey moduleKey, int answeredQuantity, int affectedCount,
        IReadOnlyList<string> affectedItemDisplayNames, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Previews.TryGetValue((siteId, moduleKey), out var existing) && existing.RequestedQuantity == answeredQuantity)
        {
            existing.Answer(affectedCount, affectedItemDisplayNames, now);
        }

        return Task.CompletedTask;
    }
}
