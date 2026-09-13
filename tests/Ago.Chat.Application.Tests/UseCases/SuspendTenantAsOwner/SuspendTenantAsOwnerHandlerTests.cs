using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SuspendTenantAsOwner;

/// <summary>
/// `22-08`/`adr/0166`: the platform owner's own account-wide freeze. This item's own Done-when,
/// restated as tests: the owner can set a duration in minutes, an already-suspended site is refused
/// (not silently re-suspended), and the published lease instant is "now plus the lease length" - never
/// the owner's own chosen duration (`Contracts.TenantSuspensionChanged`'s own remarks on why a module
/// must never be told that number).
/// </summary>
public class SuspendTenantAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly SuspensionLeaseOptions LeaseOptions = new() { LeaseLength = TimeSpan.FromMinutes(5) };

    private sealed record Fixture(
        Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwnerHandler Handler,
        FakeSiteRepository Sites,
        FakeUnitOfWork UnitOfWork,
        FakeSiteSuspensionRecordRepository Records,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(Site? site = null)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(site ?? new Site(SiteId, "shop_7f3a", []));
        var unitOfWork = new FakeUnitOfWork();
        var records = new FakeSiteSuspensionRecordRepository();
        var outbox = new FakeOutboxWriter();

        var handler = new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwnerHandler(
            sites, unitOfWork, records, outbox, LeaseOptions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, unitOfWork, records, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenValid_SetsSuspendedUntilToNowPlusTheDuration()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, "suspected abuse"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(30), result.Value);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(Now.AddMinutes(30), saved!.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenValid_CommitsOneTransaction()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, "suspected abuse"),
            CancellationToken.None);

        Assert.Equal(1, fixture.UnitOfWork.TransactionsBegun);
        Assert.Equal(1, fixture.UnitOfWork.TransactionsCommitted);
    }

    [Fact]
    public async Task HandleAsync_WhenValid_RecordsTheAct()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, "suspected abuse"),
            CancellationToken.None);

        var record = Assert.Single(fixture.Records.Recorded);
        Assert.Equal(SiteId, record.SiteId);
        Assert.Equal("Suspended", record.Action);
        Assert.Equal("owner-sub", record.PerformedBy);
        Assert.Equal("suspected abuse", record.Reason);
        Assert.Equal(Now.AddMinutes(30), record.SuspendedUntil);
    }

    /// <summary>`adr/0166`'s own load-bearing distinction: the lease instant a module is told is "now
    /// plus the lease length", never the owner's own 30-minute duration - the two numbers are
    /// independent by design.</summary>
    [Fact]
    public async Task HandleAsync_WhenValid_PublishesTheLeaseInstant_NotTheOwnersOwnDuration()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, "suspected abuse"),
            CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(TenantSuspensionChanged), envelope.Type);
        var contract = System.Text.Json.JsonSerializer.Deserialize<TenantSuspensionChanged>(envelope.Payload)!;
        Assert.Equal(SiteId.Value, contract.SiteId);
        Assert.Equal(Now.AddMinutes(5), contract.SuspendedUntil);
        Assert.NotEqual(Now.AddMinutes(30), contract.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadySuspended_ReturnsAlreadySuspended_AndWritesNothing()
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        site.Suspend(Now.AddMinutes(10), Now);
        site.ClearDomainEvents();
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, "another reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.AlreadySuspended", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        Assert.Empty(fixture.Records.Recorded);
        Assert.Equal(Now.AddMinutes(10), site.SuspendedUntil);
    }

    [Fact]
    public async Task HandleAsync_WhenAPreviousSuspensionHasAlreadyPassed_SucceedsAsANewSuspension()
    {
        var site = new Site(SiteId, "shop_7f3a", []);
        site.Suspend(Now.AddMinutes(-1), Now.AddMinutes(-10));
        site.ClearDomainEvents();
        var fixture = CreateFixture(site);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 15, "a new incident"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(15), site.SuspendedUntil);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task HandleAsync_WithANonPositiveDuration_ReturnsDurationInvalid_AndWritesNothing(int minutes)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", minutes, "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.DurationInvalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WithABlankReason_ReturnsReasonRequired_AndWritesNothing(string? reason)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(SiteId, "owner-sub", 30, reason!),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TenantSuspension.ReasonRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SuspendTenantAsOwner.SuspendTenantAsOwner(
                new SiteId(Guid.NewGuid()), "owner-sub", 30, "reason"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }
}
