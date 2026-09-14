using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ExtendSuspensionAsOwner;
using Ago.Chat.Application.UseCases.GetSuspensionStatusForSite;
using Ago.Chat.Application.UseCases.SuspendTenantAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-70`: `docs/backlog/25-70-*.md`'s own Done-when, proven against real Postgres rather than a
/// hand-seeded row - a real account is suspended through the same
/// <see cref="SuspendTenantAsOwnerHandler"/> the platform owner's own console calls
/// (<see cref="Api.Owner.OwnerSuspensionEndpoints"/>), and this item's own new read
/// (<see cref="ISiteSuspensionReadStore.GetForTenantAsync"/>, resolved through
/// <see cref="GetSuspensionStatusForSiteHandler"/>) is what a suspended tenant's own console loads.
///
/// <para><b>Why this needs the real handler, not <c>Site.Suspend</c> called directly.</b>
/// <see cref="ISiteSuspensionReadStore.GetForTenantAsync"/>'s own "since when" is read off
/// <c>site_suspensions</c>' own most recent <c>'Suspended'</c> row - a fact only
/// <see cref="SuspendTenantAsOwnerHandler"/> (via <see cref="ISiteSuspensionRecordRepository"/>) ever
/// writes. <c>TenantSuspensionSessionGateTests</c> calls <c>Site.Suspend</c> directly precisely because
/// its own gate does not depend on that audit row; this file's own read does, so it is exactly the case
/// that shortcut would not prove.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantSuspensionStatusReadTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset SuspendedAt = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly SuspensionLeaseOptions LeaseOptions = new() { LeaseLength = TimeSpan.FromMinutes(5) };

    [Fact]
    public async Task GetForTenantAsync_ForASiteSuspendedThroughTheRealHandler_ReportsSuspendedSinceAndUntil()
    {
        var siteId = await SeedSiteAsync();

        DateTimeOffset until;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = CreateSuspendHandler(db, SuspendedAt);
            var result = await handler.HandleAsync(
                new SuspendTenantAsOwner(siteId, "owner-sub", DurationMinutes: 60, "suspected abuse"),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
            until = result.Value;
        }

        var readStore = new SiteSuspensionReadStore(fixture.DataSource);
        var status = await readStore.GetForTenantAsync(siteId, SuspendedAt.AddMinutes(1), CancellationToken.None);

        Assert.True(status.IsSuspended);
        Assert.Equal(SuspendedAt, status.Since);
        Assert.Equal(until, status.Until);
    }

    // The subtle case this method's own SQL exists to get right: extending a suspension writes a new
    // `'Extended'` row and pushes `suspended_until` further out, but the suspension has been
    // continuously in effect since the original `'Suspended'` act, not since the extension - "since
    // when" must not move just because someone extended the deadline.
    [Fact]
    public async Task GetForTenantAsync_AfterAnExtension_KeepsTheOriginalSinceButMovesUntil()
    {
        var siteId = await SeedSiteAsync();

        DateTimeOffset originalUntil;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = CreateSuspendHandler(db, SuspendedAt);
            var result = await handler.HandleAsync(
                new SuspendTenantAsOwner(siteId, "owner-sub", DurationMinutes: 30, "suspected abuse"),
                CancellationToken.None);
            originalUntil = result.Value;
        }

        var extendedAt = SuspendedAt.AddMinutes(10);
        DateTimeOffset extendedUntil;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = CreateExtendHandler(db, extendedAt);
            var result = await handler.HandleAsync(
                new ExtendSuspensionAsOwner(siteId, "owner-sub", AdditionalMinutes: 45, "still investigating"),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
            extendedUntil = result.Value;
        }

        Assert.True(extendedUntil > originalUntil);

        var readStore = new SiteSuspensionReadStore(fixture.DataSource);
        var status = await readStore.GetForTenantAsync(siteId, extendedAt.AddMinutes(1), CancellationToken.None);

        Assert.True(status.IsSuspended);
        Assert.Equal(SuspendedAt, status.Since); // unmoved by the extension
        Assert.Equal(extendedUntil, status.Until);
    }

    [Fact]
    public async Task GetForTenantAsync_ForASiteThatWasNeverSuspended_ReportsNotSuspended()
    {
        var siteId = await SeedSiteAsync();

        var readStore = new SiteSuspensionReadStore(fixture.DataSource);
        var status = await readStore.GetForTenantAsync(siteId, SuspendedAt, CancellationToken.None);

        Assert.False(status.IsSuspended);
        Assert.Null(status.Since);
        Assert.Null(status.Until);
    }

    // `25-70`'s own tenant-scoping requirement, proven against real rows rather than a permission-check
    // unit test alone: suspending one site's own account must not leak into another site's own read -
    // the same real query, scoped by SiteId, run for a sibling site that was never touched.
    [Fact]
    public async Task GetForTenantAsync_ForASiteThatIsNotTheOneSuspended_ReportsNotSuspended()
    {
        var suspendedSiteId = await SeedSiteAsync();
        var otherSiteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var handler = CreateSuspendHandler(db, SuspendedAt);
            var result = await handler.HandleAsync(
                new SuspendTenantAsOwner(suspendedSiteId, "owner-sub", DurationMinutes: 60, "suspected abuse"),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        var readStore = new SiteSuspensionReadStore(fixture.DataSource);
        var suspendedStatus = await readStore.GetForTenantAsync(suspendedSiteId, SuspendedAt.AddMinutes(1), CancellationToken.None);
        var otherStatus = await readStore.GetForTenantAsync(otherSiteId, SuspendedAt.AddMinutes(1), CancellationToken.None);

        Assert.True(suspendedStatus.IsSuspended);
        Assert.False(otherStatus.IsSuspended);
        Assert.Null(otherStatus.Since);
        Assert.Null(otherStatus.Until);
    }

    // The full assembled path a suspended tenant's own console actually calls -
    // GetSuspensionStatusForSiteHandler, not the read store directly - proving the permission gate and
    // the real read agree: the operator who holds SiteConfigure on the suspended site sees it, and
    // would be refused (Application-layer coverage: GetSuspensionStatusForSiteHandlerTests) for a site
    // it does not hold that permission on.
    [Fact]
    public async Task GetSuspensionStatusForSiteHandler_ForAnOperatorOfTheSuspendedSite_ReturnsItsSuspensionState()
    {
        var siteId = await SeedSiteAsync();
        var operatorId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var handler = CreateSuspendHandler(db, SuspendedAt);
            var result = await handler.HandleAsync(
                new SuspendTenantAsOwner(siteId, "owner-sub", DurationMinutes: 60, "suspected abuse"),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        var permissions = new SingleGrantPermissionChecker(operatorId, siteId, Permission.SiteConfigure);
        var readStore = new SiteSuspensionReadStore(fixture.DataSource);
        var statusHandler = new GetSuspensionStatusForSiteHandler(readStore, permissions, new FixedClock(SuspendedAt.AddMinutes(1)));

        var result2 = await statusHandler.HandleAsync(
            new GetSuspensionStatusForSite(operatorId, siteId), CancellationToken.None);

        Assert.True(result2.IsSuccess);
        Assert.True(result2.Value.IsSuspended);
        Assert.Equal(SuspendedAt, result2.Value.Since);
    }

    private static SuspendTenantAsOwnerHandler CreateSuspendHandler(AgoChatDbContext db, DateTimeOffset now) =>
        new(
            new SiteRepository(db), new EfUnitOfWork(db), new SiteSuspensionRecordRepository(db),
            new EfOutboxWriter<AgoChatDbContext>(db), LeaseOptions, new UuidV7Generator(), new FixedClock(now));

    private static ExtendSuspensionAsOwnerHandler CreateExtendHandler(AgoChatDbContext db, DateTimeOffset now) =>
        new(
            new SiteRepository(db), new EfUnitOfWork(db), new SiteSuspensionRecordRepository(db),
            new EfOutboxWriter<AgoChatDbContext>(db), LeaseOptions, new UuidV7Generator(), new FixedClock(now));

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://shop.example"]));
        await db.SaveChangesAsync();
        return siteId;
    }

    /// <summary>A minimal <see cref="IPermissionChecker"/> stand-in local to this file, rather than
    /// pulling in <c>Ago.Chat.Application.Tests</c>' own <c>FakePermissionChecker</c> - this project
    /// carries no reference to that test project (its own real-Postgres purpose is orthogonal to that
    /// project's fakes), and this single-grant shape is all
    /// <see cref="GetSuspensionStatusForSiteHandler"/>'s own permission check needs from it.</summary>
    private sealed class SingleGrantPermissionChecker(OperatorId operatorId, SiteId siteId, Permission permission)
        : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(
            OperatorId requestedBy, SiteId requestedSiteId, Permission requestedPermission, CancellationToken cancellationToken) =>
            Task.FromResult(requestedBy == operatorId && requestedSiteId == siteId && requestedPermission == permission);

        public Task<IReadOnlyList<string>> GetPermissionsAsync(
            OperatorId requestedBy, SiteId requestedSiteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(
                requestedBy == operatorId && requestedSiteId == siteId ? [permission.Value] : []);

        public Task<int> CountNonRemovedHoldersAsync(
            SiteId requestedSiteId, Permission requestedPermission, CancellationToken cancellationToken) =>
            Task.FromResult(requestedSiteId == siteId && requestedPermission == permission ? 1 : 0);
    }

    /// <summary>The same per-file local <c>IClock</c> stand-in every other file in this project already
    /// defines for itself (e.g. <c>AddRolePermissionsAsOwnerHandlerTests</c>) rather than a shared one -
    /// there is no shared fake clock in this project to reuse.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
