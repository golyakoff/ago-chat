using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-33`: the read side's own redaction, against a real Postgres - the direct proof that a removed
/// message's <c>Body</c> never crosses out of <c>Ago.Chat.Infrastructure.Postgres</c>, across all
/// three of <see cref="TeamMessageReadStore"/>'s own queries, not merely that the write side stores a
/// <c>removed_at</c> column (<see cref="TeamChatRepositoryTests"/> proves that half). See
/// <c>Ago.Chat.Application.Abstractions.TeamMessageHistoryItem</c>'s own remarks for why this
/// redaction happens here and not at rest.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TeamMessageReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId SiteId, OperatorId OperatorId, TeamMessageId MessageId)> SeedRemovedMessageAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        var messageId = new TeamMessageId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await repository.PostAsync(
                siteId, operatorId, false, new MessageBody("something regrettable"), null, messageId, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            var message = await repository.GetByIdAsync(messageId, CancellationToken.None);
            message!.Remove(Now.AddMinutes(1));
            await repository.RemoveAsync(message, operatorId, Guid.NewGuid(), Now.AddMinutes(1), CancellationToken.None);
        }

        return (siteId, operatorId, messageId);
    }

    [Fact]
    public async Task GetHistoryAsync_ForARemovedMessage_ReturnsNullBody_AndTheRemovalTimestamp()
    {
        var (siteId, _, messageId) = await SeedRemovedMessageAsync();
        var readStore = new TeamMessageReadStore(fixture.DataSource);

        var page = await readStore.GetHistoryAsync(siteId, beforeSequence: null, pageSize: 50, CancellationToken.None);

        var item = Assert.Single(page.Messages);
        Assert.Equal(messageId, item.Id);
        Assert.Null(item.Body);
        Assert.Equal(Now.AddMinutes(1), item.RemovedAt);
    }

    [Fact]
    public async Task GetDeltaAsync_ForARemovedMessage_ReturnsNullBody_AndTheRemovalTimestamp()
    {
        var (siteId, _, messageId) = await SeedRemovedMessageAsync();
        var readStore = new TeamMessageReadStore(fixture.DataSource);

        var delta = await readStore.GetDeltaAsync(siteId, afterSequence: 0, CancellationToken.None);

        var item = Assert.Single(delta);
        Assert.Equal(messageId, item.Id);
        Assert.Null(item.Body);
        Assert.Equal(Now.AddMinutes(1), item.RemovedAt);
    }

    [Fact]
    public async Task GetBySequenceAsync_ForARemovedMessage_ReturnsNullBody_AndTheRemovalTimestamp()
    {
        // The exact re-read OperatorHub.RemoveTeamMessageAsync's own local echo makes, and
        // ResolveTeamMessageRemovalDeliveryTargetsHandler's own fan-out re-read - both must see the
        // redacted body, never the original.
        var (siteId, _, _) = await SeedRemovedMessageAsync();
        var readStore = new TeamMessageReadStore(fixture.DataSource);

        var item = await readStore.GetBySequenceAsync(siteId, sequence: 1, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Null(item.Body);
        Assert.NotNull(item.RemovedAt);
    }

    [Fact]
    public async Task GetHistoryAsync_ForAMessageNobodyHasRemoved_StillReturnsItsBody()
    {
        // The negative case: redaction is conditional on RemovedAt, not applied unconditionally to
        // every row this read store touches.
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await repository.PostAsync(
                siteId, operatorId, false, new MessageBody("still here"), null, new TeamMessageId(Guid.NewGuid()), Now,
                CancellationToken.None);
        }

        var readStore = new TeamMessageReadStore(fixture.DataSource);
        var page = await readStore.GetHistoryAsync(siteId, beforeSequence: null, pageSize: 50, CancellationToken.None);

        var item = Assert.Single(page.Messages);
        Assert.Equal("still here", item.Body);
        Assert.Null(item.RemovedAt);
    }
}
