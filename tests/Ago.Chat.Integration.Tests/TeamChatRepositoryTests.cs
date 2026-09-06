using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-32`'s <see cref="TeamChatRepository"/>, against a real Postgres: the compare-and-set sequence
/// (CLAUDE.md rule 8), the outbox row staged alongside the message row in one `SaveChangesAsync`
/// (rule 4), and the retry-dedup contract `ITeamChatRepository.PostAsync`'s own doc comment states.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TeamChatRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedTenantAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    [Fact]
    public async Task PostAsync_WritesTheMessageAndOneMatchingOutboxRow_InTheSameSave()
    {
        var (siteId, operatorId) = await SeedTenantAsync();
        var id = new TeamMessageId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            var message = await repository.PostAsync(
                siteId, operatorId, authorIsAdmin: false, new MessageBody("hello team"), clientMessageId: null, id, Now,
                CancellationToken.None);

            Assert.Equal(1, message.Sequence);
        }

        await using var verify = fixture.CreateDbContext();
        var row = await verify.TeamMessages.SingleAsync(m => m.Id == id, CancellationToken.None);
        Assert.Equal(siteId, row.SiteId);
        Assert.Equal("hello team", row.Body.Value);
        Assert.False(row.AuthorIsAdmin);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(o => o.Id == id.Value, CancellationToken.None);
        Assert.Equal(nameof(TeamMessagePosted), outboxRow.Type);
        Assert.Equal(siteId.Value.ToString(), outboxRow.PartitionKey);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task PostAsync_AssignsStrictlyIncreasingSequencesPerSite_NeverPerProcess()
    {
        var (siteA, operatorA) = await SeedTenantAsync();
        var (siteB, operatorB) = await SeedTenantAsync();

        await using var db = fixture.CreateDbContext();
        var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());

        var a1 = await repository.PostAsync(siteA, operatorA, false, new MessageBody("a1"), null, new TeamMessageId(Guid.NewGuid()), Now, CancellationToken.None);
        // A different site's own first message must also start at 1 - the counter is per-room
        // (CLAUDE.md rule 6: "message order is guaranteed per conversation, never globally"), not a
        // single shared counter this repository happens to serve every tenant from.
        var b1 = await repository.PostAsync(siteB, operatorB, false, new MessageBody("b1"), null, new TeamMessageId(Guid.NewGuid()), Now, CancellationToken.None);
        var a2 = await repository.PostAsync(siteA, operatorA, false, new MessageBody("a2"), null, new TeamMessageId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.Equal(1, a1.Sequence);
        Assert.Equal(1, b1.Sequence);
        Assert.Equal(2, a2.Sequence);
    }

    /// <summary>The concurrency proof CLAUDE.md's own rules ask for: N concurrent posts to the *same*
    /// site, each on its own `DbContext`/connection (a real cross-request race, not one serialized by
    /// sharing a single change tracker), must produce N distinct, gapless-or-not-but-never-duplicate
    /// sequence numbers - the atomic `UPDATE ... RETURNING` is what this proves, not merely
    /// documents.</summary>
    [Fact]
    public async Task PostAsync_UnderConcurrentSendsToTheSameSite_NeverAssignsTheSameSequenceTwice()
    {
        var (siteId, operatorId) = await SeedTenantAsync();
        const int concurrency = 20;

        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            await using var db = fixture.CreateDbContext();
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            return await repository.PostAsync(
                siteId, operatorId, false, new MessageBody($"concurrent {i}"), null, new TeamMessageId(Guid.NewGuid()), Now,
                CancellationToken.None);
        });

        var results = await Task.WhenAll(tasks);

        var sequences = results.Select(r => r.Sequence).OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, concurrency), sequences);
    }

    [Fact]
    public async Task PostAsync_WhenTheSameClientMessageIdIsRetried_ReturnsTheOriginalMessage_WithoutPostingASecondOne()
    {
        var (siteId, operatorId) = await SeedTenantAsync();
        var clientMessageId = Guid.NewGuid();

        await using var db = fixture.CreateDbContext();
        var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());

        var first = await repository.PostAsync(
            siteId, operatorId, false, new MessageBody("original"), clientMessageId, new TeamMessageId(Guid.NewGuid()), Now,
            CancellationToken.None);

        // A genuinely different attempt (its own new TeamMessageId, the shape a real retry after a
        // SendOutcomeUnknownError-style failure takes - `5-07`'s own protocol) carrying the *same*
        // clientMessageId.
        var retry = await repository.PostAsync(
            siteId, operatorId, false, new MessageBody("original"), clientMessageId, new TeamMessageId(Guid.NewGuid()), Now,
            CancellationToken.None);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.Sequence, retry.Sequence);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.TeamMessages.CountAsync(m => m.SiteId == siteId, CancellationToken.None));
    }

    // -------------------------------------------------------------------------------------------
    // `23-33`: RemoveAsync/GetByIdAsync - the tombstone, the removal's own small accountability
    // record, and the outbox row that drives the tombstone's realtime push, all inside one
    // SaveChangesAsync (CLAUDE.md rule 4, the same atomicity PostAsync's own tests above already
    // prove for the send path).
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveAsync_TombstonesTheMessage_WritesTheRemovalRecord_AndStagesTheOutboxRow_InTheSameSave()
    {
        var (siteId, operatorId) = await SeedTenantAsync();
        var removerId = new OperatorId(Guid.NewGuid());
        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.Operators.Add(new Operator(removerId, siteId, OperatorStatus.Online, capacity: 5));
            await seedDb.SaveChangesAsync();
        }

        var messageId = new TeamMessageId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await repository.PostAsync(
                siteId, operatorId, false, new MessageBody("please remove this"), null, messageId, Now, CancellationToken.None);
        }

        var removalId = Guid.NewGuid();
        var removedAt = Now.AddMinutes(3);
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            var message = await repository.GetByIdAsync(messageId, CancellationToken.None);
            Assert.NotNull(message);
            Assert.Null(message.RemovedAt);

            message.Remove(removedAt);
            await repository.RemoveAsync(message, removerId, removalId, removedAt, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();

        var row = await verify.TeamMessages.SingleAsync(m => m.Id == messageId, CancellationToken.None);
        Assert.Equal(removedAt, row.RemovedAt);
        // `23-33`'s own tombstone choice: the original text stays at rest, never scrubbed by the
        // write side - Ago.Chat.Domain.TeamMessage.RemovedAt's own remarks explain why. Only the
        // read side (TeamMessageReadStore, proven below) hides it from every future read.
        Assert.Equal("please remove this", row.Body.Value);

        var removal = await verify.TeamMessageRemovals.SingleAsync(r => r.TeamMessageId == messageId, CancellationToken.None);
        Assert.Equal(removalId, removal.Id);
        Assert.Equal(siteId, removal.SiteId);
        Assert.Equal(removerId, removal.RemovedByOperatorId);
        Assert.Equal(removedAt, removal.RemovedAt);

        // Scoped by PartitionKey (the site), not merely by Type - the shared Postgres fixture this
        // collection uses is not reset between tests, so more than one TeamMessageRemoved row can
        // exist across the whole run; this test's own siteId is what makes the row it wrote unique.
        var outboxRow = await verify.Set<OutboxMessage>()
            .SingleAsync(o => o.Type == nameof(TeamMessageRemoved) && o.PartitionKey == siteId.Value.ToString(), CancellationToken.None);
        Assert.Equal(siteId.Value.ToString(), outboxRow.PartitionKey);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task RemoveAsync_WritesExactlyOneRemovalRecord_NotOnePerColumnItTouches()
    {
        // A belt-and-braces check on top of RemoveTeamMessageHandlerTests' own idempotency proof
        // (fakes only) - the real unique index (ix_team_message_removals_team_message_id) is what
        // actually stops two removal rows for the same message from ever existing, and this proves
        // the index does not reject the one legitimate write that must succeed.
        var (siteId, operatorId) = await SeedTenantAsync();
        var messageId = new TeamMessageId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await repository.PostAsync(
                siteId, operatorId, false, new MessageBody("only once"), null, messageId, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            var message = await repository.GetByIdAsync(messageId, CancellationToken.None);
            message!.Remove(Now.AddMinutes(1));
            await repository.RemoveAsync(message, operatorId, Guid.NewGuid(), Now.AddMinutes(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.TeamMessageRemovals.CountAsync(r => r.TeamMessageId == messageId, CancellationToken.None));
    }
}
