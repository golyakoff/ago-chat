using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-82`: <see cref="SiteErasurePublisher"/>'s own Done-when, against a real Postgres - the site
/// delete and the <see cref="SiteErased"/> outbox row commit together (rule 4), and a retried or
/// racing call against an already-gone site stages nothing a second time (rule 5). The consumer half -
/// draining `ago-calendar`'s own `role_assignment_projections` for the tenant this event names - lives
/// in `ago-calendar`, a different repository with a different database; this file cannot reach it and
/// does not try to, the identical boundary <see cref="RoleAssignmentsChangedOutboxTests"/>'s own class
/// remarks already draw for the sibling event.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SiteErasedOutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ErasingASite_DeletesTheRow_AndStagesExactlyOneSiteErasedRow_InTheSameCommit()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        bool erased;
        await using (var db = fixture.CreateDbContext())
        {
            var publisher = new SiteErasurePublisher(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            erased = await publisher.EraseAndPublishAsync(siteId, Now, CancellationToken.None);
        }

        Assert.True(erased);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.Sites.CountAsync(s => s.Id == siteId, CancellationToken.None));

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(SiteErased) && o.PartitionKey == siteId.Value.ToString())
            .ToListAsync(CancellationToken.None);
        var outboxRow = Assert.Single(outboxRows);

        var contract = System.Text.Json.JsonSerializer.Deserialize<SiteErased>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal(Now, contract.OccurredAt);
        Assert.Null(outboxRow.PublishedAt);
    }

    /// <summary>
    /// `CLAUDE.md` rule 5: a second call against a site the first call already erased - a retry after a
    /// crash between this commit and a later step in <c>DemoTenantExpiryJob.RemoveAsync</c>, or two
    /// replicas racing the same tenant - is not an error and does not double the fact. The affected-row
    /// count gates the outbox write, so this is provable at the row-count level rather than only by
    /// absence of an exception.
    /// </summary>
    [Fact]
    public async Task ErasingTheSameSiteTwice_TheSecondCallIsANoOp_NoSecondSiteErasedRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var publisher = new SiteErasurePublisher(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            Assert.True(await publisher.EraseAndPublishAsync(siteId, Now, CancellationToken.None));
        }

        bool secondCallErased;
        await using (var db = fixture.CreateDbContext())
        {
            var publisher = new SiteErasurePublisher(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            secondCallErased = await publisher.EraseAndPublishAsync(siteId, Now.AddSeconds(1), CancellationToken.None);
        }

        Assert.False(secondCallErased);

        await using var verify = fixture.CreateDbContext();
        var outboxRowCount = await verify.Set<OutboxMessage>()
            .CountAsync(o => o.Type == nameof(SiteErased) && o.PartitionKey == siteId.Value.ToString(), CancellationToken.None);
        Assert.Equal(1, outboxRowCount);
    }

    /// <summary>A site that never existed at all - the same "no fallback, ever" refusal-not-guess shape
    /// this codebase already applies elsewhere, exercised at this port's own boundary.</summary>
    [Fact]
    public async Task ErasingASiteThatNeverExisted_IsANoOp_NoSiteErasedRow()
    {
        var siteId = new SiteId(Guid.NewGuid());

        bool erased;
        await using (var db = fixture.CreateDbContext())
        {
            var publisher = new SiteErasurePublisher(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            erased = await publisher.EraseAndPublishAsync(siteId, Now, CancellationToken.None);
        }

        Assert.False(erased);

        await using var verify = fixture.CreateDbContext();
        var outboxRowCount = await verify.Set<OutboxMessage>()
            .CountAsync(o => o.Type == nameof(SiteErased) && o.PartitionKey == siteId.Value.ToString(), CancellationToken.None);
        Assert.Equal(0, outboxRowCount);
    }
}
