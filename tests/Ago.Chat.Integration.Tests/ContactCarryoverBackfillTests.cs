using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Backfill;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-59`/`adr/0147`: the retroactive carry-over pass, against real Postgres. Every test seeds
/// <c>Sites</c>/<c>Visitors</c>/<c>VisitorContactDetails</c> directly - the same "no publisher runs, this
/// is a tenant with real pre-existing history" shape <c>RoleAssignmentProjectionBackfillTests</c> already
/// establishes for its own retroactive pass, and <see cref="ContactCarryoverRequestStore"/> is what
/// <see cref="EnableModuleForSiteAsOwnerHandler"/> would have called at grant time - exercised here
/// directly, the same "request, then drive the batches" split the item's own report has to demonstrate.
/// </summary>
[Collection(ContactCarryoverBackfillCollection.Name)]
public sealed class ContactCarryoverBackfillTests(ContactCarryoverBackfillFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task ASiteWithNoRequest_RunOneBatchAsync_ReportsNothingPending_AndStagesNothing()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        var backfill = new ContactCarryoverBackfill(db, new UuidV7Generator(), new FixedClock(Now));

        var outcome = await backfill.RunOneBatchAsync(siteId, batchSize: 10, CancellationToken.None);

        Assert.Equal(ContactCarryoverBatchOutcome.NothingPending, outcome);
    }

    /// <summary>
    /// The item's own Done-when: "granting the module carries over contacts collected before the
    /// grant, shown working on a tenant with pre-existing contacts." Seven contacts, a batch size of
    /// three - three calls to drain it, the last one short, exactly proving the batching rather than
    /// asserting the end state alone.
    /// </summary>
    [Fact]
    public async Task ASiteWithSevenPreExistingContacts_DrainsInBoundedBatches_AndStagesOneContactCollectedPerContact()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var contactIds = await SeedSiteWithContactsAsync(siteId, visitorId, count: 7);

        await using (var requestDb = fixture.CreateDbContext())
        {
            await new ContactCarryoverRequestStore(requestDb).RequestAsync(siteId, Now, CancellationToken.None);
        }

        var backfill = new ContactCarryoverBackfill(fixture.CreateDbContext(), new UuidV7Generator(), new FixedClock(Now));

        var first = await backfill.RunOneBatchAsync(siteId, batchSize: 3, CancellationToken.None);
        Assert.Equal(3, first.Published);
        Assert.False(first.Completed);

        var second = await backfill.RunOneBatchAsync(siteId, batchSize: 3, CancellationToken.None);
        Assert.Equal(3, second.Published);
        Assert.False(second.Completed);

        var third = await backfill.RunOneBatchAsync(siteId, batchSize: 3, CancellationToken.None);
        Assert.Equal(1, third.Published);
        Assert.True(third.Completed);

        // A fourth call finds nothing left pending - the request completed, and re-checking it is
        // cheap and correct rather than an error.
        var fourth = await backfill.RunOneBatchAsync(siteId, batchSize: 3, CancellationToken.None);
        Assert.Equal(ContactCarryoverBatchOutcome.NothingPending, fourth);

        await using var verify = fixture.CreateDbContext();
        var stagedContactIds = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ContactCollected))
            .Select(o => o.PartitionKey)
            .ToListAsync(CancellationToken.None);
        Assert.Equal(
            contactIds.Select(id => id.ToString()).OrderBy(x => x, StringComparer.Ordinal),
            stagedContactIds.OrderBy(x => x, StringComparer.Ordinal));

        var request = await verify.ContactCarryoverRequests.SingleAsync(r => r.SiteId == siteId, CancellationToken.None);
        Assert.NotNull(request.CompletedAt);
        Assert.Equal(contactIds[^1], request.CursorContactId);
    }

    /// <summary>
    /// Done-when: "a failed carry-over can be re-run without re-granting anything." Simulated the
    /// honest way - a batch call is never made for the second half of a site's history (standing in for
    /// a crash between cycles), and the very next call to <see cref="ContactCarryoverBackfill.RunOneBatchAsync"/>
    /// (the next sweep tick, in production) finishes the job with no second call to
    /// <see cref="ContactCarryoverRequestStore.RequestAsync"/> anywhere in this test.
    /// </summary>
    [Fact]
    public async Task AnInterruptedCarryover_ResumesFromItsOwnCursor_WithNoSecondRequest()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var contactIds = await SeedSiteWithContactsAsync(siteId, visitorId, count: 5);

        await using (var requestDb = fixture.CreateDbContext())
        {
            await new ContactCarryoverRequestStore(requestDb).RequestAsync(siteId, Now, CancellationToken.None);
        }

        // "The process died after the first batch" - the next call is a brand-new ContactCarryoverBackfill
        // instance, the same as a fresh Worker tick would construct, reading only what the previous
        // batch persisted.
        await using (var db1 = fixture.CreateDbContext())
        {
            var firstAttempt = new ContactCarryoverBackfill(db1, new UuidV7Generator(), new FixedClock(Now));
            var outcome = await firstAttempt.RunOneBatchAsync(siteId, batchSize: 2, CancellationToken.None);
            Assert.Equal(2, outcome.Published);
            Assert.False(outcome.Completed);
        }

        // No RequestAsync call here - this is the resumption, driven purely by the persisted cursor.
        await using (var db2 = fixture.CreateDbContext())
        {
            var resumed = new ContactCarryoverBackfill(db2, new UuidV7Generator(), new FixedClock(Now.AddMinutes(1)));
            var second = await resumed.RunOneBatchAsync(siteId, batchSize: 2, CancellationToken.None);
            Assert.Equal(2, second.Published);
            Assert.False(second.Completed);

            var third = await resumed.RunOneBatchAsync(siteId, batchSize: 2, CancellationToken.None);
            Assert.Equal(1, third.Published);
            Assert.True(third.Completed);
        }

        await using var verify = fixture.CreateDbContext();
        var stagedCount = await verify.Set<OutboxMessage>().CountAsync(
            o => o.Type == nameof(ContactCollected), CancellationToken.None);
        Assert.Equal(5, stagedCount);

        // No duplicate: each of the five contacts was staged exactly once across the interrupted-then-
        // resumed run, never re-staged from the start.
        var distinctPartitionKeys = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ContactCollected))
            .Select(o => o.PartitionKey)
            .Distinct()
            .CountAsync(CancellationToken.None);
        Assert.Equal(5, distinctPartitionKeys);
    }

    /// <summary>A re-grant (`IContactCarryoverRequestStore.RequestAsync` called twice for the same
    /// site) resets the request - the same repair-by-ordinary-grant path `23-102` established for
    /// permissions, applied here to a request row instead of a role's own column.</summary>
    [Fact]
    public async Task RequestingTwice_ForTheSameSite_ResetsTheRequest_RatherThanErroring()
    {
        await fixture.ResetAsync();

        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await SeedSiteWithContactsAsync(siteId, visitorId, count: 2);

        await using (var db1 = fixture.CreateDbContext())
        {
            await new ContactCarryoverRequestStore(db1).RequestAsync(siteId, Now, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            var backfill = new ContactCarryoverBackfill(db2, new UuidV7Generator(), new FixedClock(Now));
            var outcome = await backfill.RunOneBatchAsync(siteId, batchSize: 10, CancellationToken.None);
            Assert.True(outcome.Completed);
        }

        // The re-grant - a second RequestAsync for the identical site.
        await using (var db3 = fixture.CreateDbContext())
        {
            await new ContactCarryoverRequestStore(db3).RequestAsync(siteId, Now.AddMinutes(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var request = await verify.ContactCarryoverRequests.SingleAsync(r => r.SiteId == siteId, CancellationToken.None);
        Assert.Null(request.CompletedAt);
        Assert.Null(request.CursorContactId);
        Assert.Equal(Now.AddMinutes(1), request.RequestedAt);
    }

    [Fact]
    public async Task ListPendingSiteIdsAsync_ReturnsOnlyIncompleteRequests_OldestFirst()
    {
        await fixture.ResetAsync();

        var completedSite = new SiteId(Guid.NewGuid());
        var pendingSiteA = new SiteId(Guid.NewGuid());
        var pendingSiteB = new SiteId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(completedSite, $"site_{completedSite.Value:N}", []));
            seed.Sites.Add(new Site(pendingSiteA, $"site_{pendingSiteA.Value:N}", []));
            seed.Sites.Add(new Site(pendingSiteB, $"site_{pendingSiteB.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ContactCarryoverRequestStore(db);
            await store.RequestAsync(pendingSiteB, Now, CancellationToken.None);
            await store.RequestAsync(completedSite, Now.AddSeconds(-10), CancellationToken.None);
            await store.RequestAsync(pendingSiteA, Now.AddSeconds(-5), CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var backfill = new ContactCarryoverBackfill(db, new UuidV7Generator(), new FixedClock(Now));
            var outcome = await backfill.RunOneBatchAsync(completedSite, batchSize: 10, CancellationToken.None);
            Assert.True(outcome.Completed);
        }

        await using var read = fixture.CreateDbContext();
        var backfillRead = new ContactCarryoverBackfill(read, new UuidV7Generator(), new FixedClock(Now));
        var pending = await backfillRead.ListPendingSiteIdsAsync(10, CancellationToken.None);

        Assert.Equal([pendingSiteA, pendingSiteB], pending);
    }

    private async Task<List<Guid>> SeedSiteWithContactsAsync(SiteId siteId, VisitorId visitorId, int count)
    {
        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        seed.Visitors.Add(new Visitor(visitorId, siteId, Now));

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var detail = VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(Guid.CreateVersion7(Now.AddSeconds(i))), visitorId,
                VisitorContactDetailKind.Phone, $"+1 555 01{i:00}", Now.AddSeconds(i));
            seed.VisitorContactDetails.Add(detail);
            ids.Add(detail.Id.Value);
        }

        await seed.SaveChangesAsync();
        return ids;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
