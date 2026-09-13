using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ExtendSuspensionAsOwner;

public class ExtendSuspensionAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly SuspensionLeaseOptions LeaseOptions = new() { LeaseLength = TimeSpan.FromMinutes(5) };

    private sealed record Fixture(
        Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwnerHandler Handler,
        FakeSiteRepository Sites,
        FakeSiteSuspensionRecordRepository Records,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(Site site)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var records = new FakeSiteSuspensionRecordRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwnerHandler(
            sites, new FakeUnitOfWork(), records, outbox, LeaseOptions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, records, outbox);
    }

    private static Site SuspendedSite(DateTimeOffset until)
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        site.Suspend(until, Now.AddMinutes(-1));
        site.ClearDomainEvents();
        return site;
    }

    /// <summary>`docs/backlog/22-08-*.md`'s own Scope: "extend (push suspended_until further out)" -
    /// added to the *current* suspended_until, not to "now" (`ExtendSuspensionAsOwner`'s own remarks
    /// for why).</summary>
    [Fact]
    public async Task HandleAsync_WhenSuspended_AddsToTheCurrentSuspendedUntil_NotToNow()
    {
        var site = SuspendedSite(Now.AddMinutes(10));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 20, "still under review"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(30), result.Value);
        Assert.Equal(Now.AddMinutes(30), site.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenSuspended_PublishesTheLeaseInstant_NotTheExtendedOwnerDuration()
    {
        var site = SuspendedSite(Now.AddMinutes(10));
        var fixture = CreateFixture(site);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 20, "still under review"),
            CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<TenantSuspensionChanged>(envelope.Payload)!;
        Assert.Equal(Now.AddMinutes(5), contract.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenNotCurrentlySuspended_ReturnsNotSuspended_AndWritesNothing()
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 20, "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.NotSuspended", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSuspensionAlreadyPassed_ReturnsNotSuspended()
    {
        var site = SuspendedSite(Now.AddMinutes(-5));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 20, "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.NotSuspended", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithANonPositiveExtension_ReturnsDurationInvalid()
    {
        var site = SuspendedSite(Now.AddMinutes(10));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 0, "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.DurationInvalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithABlankReason_ReturnsReasonRequired()
    {
        var site = SuspendedSite(Now.AddMinutes(10));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExtendSuspensionAsOwner.ExtendSuspensionAsOwner(SiteId, "owner-sub", 10, ""),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.ReasonRequired", result.Error!.Value.Code);
    }
}
