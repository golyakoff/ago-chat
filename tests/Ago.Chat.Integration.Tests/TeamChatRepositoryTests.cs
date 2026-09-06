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
}
