using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `25-72`'s own Done-when: "two `SiteExportJob` instances ticking concurrently against the same
/// database claim disjoint batches - proven by actually running two instances against one database,
/// not by reading the query." Exercises <see cref="SiteExportClaimQuery"/> directly - the Done-when's
/// own stated alternative to driving two full <see cref="SiteExportJob"/> instances - the same
/// "prove the claim mechanism itself, not the whole archive-build/upload pipeline built on top of it"
/// scoping this file's own sibling <c>ConversationAssignmentConcurrencyTests</c> already applies to a
/// different claim query.
///
/// <para><b>Lives here, in <c>Ago.Chat.Concurrency.Tests</c>, not beside <c>SiteExportPruneJobTests</c>
/// in <c>Ago.Chat.Integration.Tests</c>.</b> <see cref="SiteExportClaimQuery.ClaimPendingBatchAsync"/>
/// is deliberately unscoped by site - a `Worker` replica claims across every tenant's queue at once,
/// the same shape the query it replaces (<c>SiteExportQuery.ListPendingAsync</c>) already had. Run
/// against <c>Ago.Chat.Integration.Tests</c>' own shared, never-truncated <c>PostgresFixture</c> (many
/// unrelated test classes' own `Pending`/`Ready`/etc. export rows accumulating in the same table across
/// a whole test run), a global claim can legitimately sweep up another test's row too - which is
/// exactly the class of failure this item's own Found note names as already having bitten
/// `SiteExportModuleGateIntegrationTests` once tonight (an exact-count assertion against a shared,
/// unscoped sweep, broken by a concurrently-seeded row). <see cref="ConcurrencyTestFixture"/> gives
/// this file its own, single-use Postgres container instead - the identical isolation
/// `ConversationAssignmentConcurrencyTests` already relies on for its own claim query. That still
/// leaves this file's own tests sharing one container *with each other* across a run (nothing else in
/// the repository touches `export_requests`, but every `[Fact]`/`[Theory]` case below does) - handled
/// by <see cref="ResetExportRequestsAsync"/>, called first by every test, rather than by hoping no
/// other case in this file left a `Pending` row an unscoped batch could pick up next.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class SiteExportClaimConcurrencyTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Called first by every test below, not only defensively: <see cref="SiteExportClaimQuery.ClaimPendingBatchAsync"/>
    /// is deliberately unscoped by site (this file's own class remarks), so a leftover `Pending` row
    /// this file's own *previous* test left unclaimed would otherwise be picked up by a *later* test's
    /// own batch - the exact failure mode this file's own class remarks describe
    /// `SiteExportModuleGateIntegrationTests` hitting for real, reproduced here at small scale between
    /// sibling `[Fact]`s in one file rather than between two unrelated classes.
    /// `ConcurrencyTestFixture`'s own container is otherwise never truncated between tests (the same
    /// "fresh ids instead" philosophy <c>PostgresFixture</c>'s own remarks state) - `export_requests`
    /// is this file's own table alone within this collection, so clearing it start-of-test is exactly
    /// as safe as the fresh-ids convention everywhere else, not a departure from it.</summary>
    private async Task ResetExportRequestsAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("delete from export_requests");
    }

    /// <summary>
    /// The Done-when itself. Seeds a real queue of `Pending` requests, then fires several concurrent
    /// claim calls - each on its own real connection, simulating several `Worker` replicas ticking at
    /// once - and asserts every seeded row was claimed by exactly one caller: covering the whole set,
    /// and never claimed twice. If `ClaimPendingBatchAsync`'s own `FOR UPDATE SKIP LOCKED` claim were
    /// only a plain read (this item's own Found bug, restated as a test - and reproduced by hand while
    /// building this test: removing `FOR UPDATE SKIP LOCKED` made this exact assertion fail with
    /// duplicate ids across callers, see this item's own report), two callers racing on the same rows
    /// would come back with overlapping ids - the property a read-only `SELECT` cannot prevent and an
    /// atomic claim-and-flip can.
    /// </summary>
    [Fact]
    public async Task ConcurrentClaims_FromMultipleReplicas_ClaimDisjointBatches_CoveringEveryPendingRowExactlyOnce()
    {
        const int requestCount = 60;
        const int callerCount = 6;
        const int batchSizePerCaller = 10; // callerCount * batchSizePerCaller == requestCount, exactly

        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        var expectedIds = await SeedPendingRequestsAsync(siteId, requestCount);

        // Several concurrent claim calls, each its own connection - the same "multiple replicas"
        // simulation `ConversationAssignmentConcurrencyTests` already establishes for a sibling claim
        // query, applied here directly to the query rather than through a whole `SiteExportJob`.
        var claims = await Task.WhenAll(Enumerable.Range(0, callerCount).Select(async _ =>
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            return await SiteExportClaimQuery.ClaimPendingBatchAsync(connection, Now, batchSizePerCaller, CancellationToken.None);
        }));

        var claimedIds = claims.SelectMany(batch => batch.Select(c => c.ExportId)).ToList();

        // Covering: every seeded row was claimed by exactly one of the concurrent callers.
        Assert.Equal(requestCount, claimedIds.Count);
        Assert.Equal(expectedIds.OrderBy(id => id), claimedIds.OrderBy(id => id));

        // Disjoint: no id appears in more than one caller's own batch - the property a plain read
        // (this item's own Found bug) cannot guarantee under real concurrency.
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());

        // Every claimed row really did flip to Processing in the database, not just in the claim's
        // own in-memory return value - and every claim carried the same site id this test seeded.
        Assert.All(claims.SelectMany(batch => batch), c => Assert.Equal(siteId.Value, c.SiteId));

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ExportRequests.AsNoTracking()
            .Where(e => expectedIds.Contains(e.Id))
            .ToListAsync();
        Assert.Equal(requestCount, rows.Count);
        Assert.All(rows, r => Assert.Equal(nameof(ExportStatus.Processing), r.Status));
        Assert.All(rows, r => Assert.NotNull(r.ProcessingStartedAt));
    }

    /// <summary>A narrower, more direct restatement of the same property above with only two callers
    /// and a batch size that would force overlap if the claim were not atomic (each caller's own
    /// requested batch size exceeds half the queue) - the literal "two `SiteExportJob` instances"
    /// framing the Done-when itself uses, rather than this file's own wider six-caller stress
    /// version.</summary>
    [Fact]
    public async Task TwoConcurrentReplicaClaims_NeverOverlap_EvenWhenBothRequestMoreThanHalfTheQueue()
    {
        const int requestCount = 20;

        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        var expectedIds = await SeedPendingRequestsAsync(siteId, requestCount);

        var (firstBatch, secondBatch) = await WhenAllTwo(
            RunClaimAsync(batchSize: 15),
            RunClaimAsync(batchSize: 15));

        var firstIds = firstBatch.Select(c => c.ExportId).ToHashSet();
        var secondIds = secondBatch.Select(c => c.ExportId).ToHashSet();

        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.Equal(requestCount, firstIds.Count + secondIds.Count);
        Assert.Equal(expectedIds.OrderBy(id => id), firstIds.Union(secondIds).OrderBy(id => id));

        async Task<IReadOnlyList<PendingExport>> RunClaimAsync(int batchSize)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            return await SiteExportClaimQuery.ClaimPendingBatchAsync(connection, Now, batchSize, CancellationToken.None);
        }
    }

    /// <summary>
    /// Asserts the claimed *set*, not the order <see cref="SiteExportClaimQuery.ClaimPendingBatchAsync"/>'s
    /// own <c>RETURNING</c> happens to produce - Postgres does not guarantee an `UPDATE ... WHERE id IN
    /// (subquery)`'s own `RETURNING` preserves the subquery's `ORDER BY` (proven while writing this
    /// test: the two claimed ids came back in the opposite order from the one seeded). What the
    /// `ORDER BY requested_at ... LIMIT` inside the claim's own subquery actually guarantees is *which*
    /// rows get claimed - the two oldest, never the newest - not what order the caller iterates them
    /// in afterward; <see cref="SiteExportJob.SweepAsync"/>'s own `foreach` over the claimed batch
    /// never depends on that order either.
    /// </summary>
    [Fact]
    public async Task ClaimPendingBatchAsync_ClaimsTheOldestRequests()
    {
        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        var oldest = await SeedRequestAsync(siteId, nameof(ExportStatus.Pending), requestedAt: Now);
        var middle = await SeedRequestAsync(siteId, nameof(ExportStatus.Pending), requestedAt: Now.AddMinutes(1));
        var newest = await SeedRequestAsync(siteId, nameof(ExportStatus.Pending), requestedAt: Now.AddMinutes(2));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var claimed = await SiteExportClaimQuery.ClaimPendingBatchAsync(connection, Now.AddMinutes(5), batchSize: 2, CancellationToken.None);

        Assert.Equal(2, claimed.Count);
        Assert.Equal(
            new[] { oldest, middle }.OrderBy(id => id),
            claimed.Select(c => c.ExportId).OrderBy(id => id));

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ExportRequests.AsNoTracking()
            .Where(e => e.Id == oldest || e.Id == middle || e.Id == newest)
            .ToDictionaryAsync(e => e.Id);
        Assert.Equal(nameof(ExportStatus.Processing), rows[oldest].Status);
        Assert.Equal(Now.AddMinutes(5), rows[oldest].ProcessingStartedAt);
        Assert.Equal(nameof(ExportStatus.Processing), rows[middle].Status);
        // Never touched - the batch size excluded it, not a bug that claimed everything regardless.
        Assert.Equal(nameof(ExportStatus.Pending), rows[newest].Status);
        Assert.Null(rows[newest].ProcessingStartedAt);
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Processing")]
    [InlineData("Failed")]
    [InlineData("Expired")]
    public async Task ClaimPendingBatchAsync_NeverClaimsARequestThatIsNotPending(string otherStatus)
    {
        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        var untouchable = await SeedRequestAsync(siteId, otherStatus, requestedAt: Now);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var claimed = await SiteExportClaimQuery.ClaimPendingBatchAsync(connection, Now, batchSize: 100, CancellationToken.None);

        Assert.DoesNotContain(claimed, c => c.ExportId == untouchable);

        await using var verify = fixture.CreateDbContext();
        var row = await verify.ExportRequests.AsNoTracking().SingleAsync(e => e.Id == untouchable);
        Assert.Equal(otherStatus, row.Status);
    }

    [Fact]
    public async Task ReclaimStaleBatchAsync_ReturnsAnAbandonedProcessingRequestToPending_AndClearsItsTimestamp()
    {
        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        // `olderThan` (the sweep's own cutoff instant) sits at Now + 1h below. `stale` was claimed at
        // Now - well before that cutoff, so it has sat Processing past the timeout. `recent` was
        // claimed at Now + 2h - *after* the cutoff instant, i.e. barely any time has passed since -
        // so it must be left alone.
        var stale = await SeedRequestAsync(siteId, nameof(ExportStatus.Processing), requestedAt: Now, processingStartedAt: Now);
        var recent = await SeedRequestAsync(
            siteId, nameof(ExportStatus.Processing), requestedAt: Now, processingStartedAt: Now.AddHours(2));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var reclaimed = await SiteExportClaimQuery.ReclaimStaleBatchAsync(connection, olderThan: Now.AddHours(1), CancellationToken.None);

        Assert.Equal(1, reclaimed);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ExportRequests.AsNoTracking()
            .Where(e => e.Id == stale || e.Id == recent)
            .ToDictionaryAsync(e => e.Id);
        Assert.Equal(nameof(ExportStatus.Pending), rows[stale].Status);
        Assert.Null(rows[stale].ProcessingStartedAt);

        // Still within the timeout at the moment of this sweep - left exactly as it was.
        Assert.Equal(nameof(ExportStatus.Processing), rows[recent].Status);
        Assert.Equal(Now.AddHours(2), rows[recent].ProcessingStartedAt);
    }

    [Fact]
    public async Task ReclaimStaleBatchAsync_LeavesPendingReadyFailedAndExpiredRequestsAlone()
    {
        await ResetExportRequestsAsync();
        var siteId = await SeedSiteAsync();
        var pending = await SeedRequestAsync(siteId, nameof(ExportStatus.Pending), requestedAt: Now);
        var ready = await SeedRequestAsync(siteId, nameof(ExportStatus.Ready), requestedAt: Now);
        var failed = await SeedRequestAsync(siteId, nameof(ExportStatus.Failed), requestedAt: Now);
        var expired = await SeedRequestAsync(siteId, nameof(ExportStatus.Expired), requestedAt: Now);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var reclaimed = await SiteExportClaimQuery.ReclaimStaleBatchAsync(connection, olderThan: Now.AddYears(1), CancellationToken.None);

        Assert.Equal(0, reclaimed);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.ExportRequests.AsNoTracking()
            .Where(e => e.Id == pending || e.Id == ready || e.Id == failed || e.Id == expired)
            .ToDictionaryAsync(e => e.Id);
        Assert.Equal(nameof(ExportStatus.Pending), rows[pending].Status);
        Assert.Equal(nameof(ExportStatus.Ready), rows[ready].Status);
        Assert.Equal(nameof(ExportStatus.Failed), rows[failed].Status);
        Assert.Equal(nameof(ExportStatus.Expired), rows[expired].Status);
    }

    private static async Task<(T1, T2)> WhenAllTwo<T1, T2>(Task<T1> first, Task<T2> second)
    {
        await Task.WhenAll(first, second);
        return (first.Result, second.Result);
    }

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<List<Guid>> SeedPendingRequestsAsync(SiteId siteId, int count)
    {
        var ids = new List<Guid>(count);
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.ExportRequests.Add(new ExportRequestEntity
            {
                Id = id,
                SiteId = siteId,
                RequestedBy = Guid.NewGuid(),
                Status = nameof(ExportStatus.Pending),
                RequestedAt = Now.AddSeconds(i),
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<Guid> SeedRequestAsync(
        SiteId siteId, string status, DateTimeOffset requestedAt, DateTimeOffset? processingStartedAt = null)
    {
        var id = Guid.NewGuid();
        await using var db = fixture.CreateDbContext();
        db.ExportRequests.Add(new ExportRequestEntity
        {
            Id = id,
            SiteId = siteId,
            RequestedBy = Guid.NewGuid(),
            Status = status,
            RequestedAt = requestedAt,
            ProcessingStartedAt = processingStartedAt,
        });
        await db.SaveChangesAsync();
        return id;
    }
}
