using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.LiftSuspensionAsOwner;

public class LiftSuspensionAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwnerHandler Handler,
        FakeSiteRepository Sites,
        FakeSiteSuspensionRecordRepository Records,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(Site site)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var records = new FakeSiteSuspensionRecordRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwnerHandler(
            sites, new FakeUnitOfWork(), records, outbox, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, records, outbox);
    }

    private static Site SuspendedSite(DateTimeOffset until)
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        site.Suspend(until, Now.AddMinutes(-1));
        site.ClearDomainEvents();
        return site;
    }

    /// <summary>`adr/0149` rule 1's own words - "chat declining to renew, plus an immediate event that
    /// brings the effect forward": this is that immediate event, and it carries null, never a lease
    /// instant.</summary>
    [Fact]
    public async Task HandleAsync_WhenSuspended_ClearsTheValue_AndPublishesNullImmediately()
    {
        var site = SuspendedSite(Now.AddMinutes(30));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwner(SiteId, "owner-sub", "resolved"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(site.SuspendedUntil);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<TenantSuspensionChanged>(envelope.Payload)!;
        Assert.Null(contract.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenSuspended_RecordsTheLift()
    {
        var site = SuspendedSite(Now.AddMinutes(30));
        var fixture = CreateFixture(site);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwner(SiteId, "owner-sub", "resolved"),
            CancellationToken.None);

        var record = Assert.Single(fixture.Records.Recorded);
        Assert.Equal("Lifted", record.Action);
        Assert.Null(record.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenNotCurrentlySuspended_ReturnsNotSuspended_AndWritesNothing()
    {
        var fixture = CreateFixture(new Site(SiteId, "shop_7f3a", []));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwner(SiteId, "owner-sub", "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.NotSuspended", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WithABlankReason_ReturnsReasonRequired()
    {
        var site = SuspendedSite(Now.AddMinutes(30));
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.LiftSuspensionAsOwner.LiftSuspensionAsOwner(SiteId, "owner-sub", "  "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.ReasonRequired", result.Error!.Value.Code);
    }
}
