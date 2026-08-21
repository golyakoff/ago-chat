using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class ConversationRepositoryTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - 2-06 partitions messages by created_at, and only the current
    // month plus the next two ever have a partition. Truncated to whole seconds so it round-trips
    // through Postgres's timestamptz unchanged (this file asserts Now against a reloaded value).
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task SaveThenReload_PreservesEveryFieldAcrossANewDbContext()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await SeedSiteVisitorOperator(siteId, visitorId, operatorId);

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AssignTo(operatorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello"), Now);
        conversation.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody("hi there"), Now);

        await using (var writeDb = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(writeDb);
            await repository.SaveAsync(conversation, CancellationToken.None);
        }

        // A fresh context proves this came from Postgres, not the first context's change tracker.
        await using var readDb = fixture.CreateDbContext();
        var reloaded = await new ConversationRepository(readDb).GetByIdAsync(conversation.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(conversation.Id, reloaded.Id);
        Assert.Equal(siteId, reloaded.SiteId);
        Assert.Equal(visitorId, reloaded.VisitorId);
        Assert.Equal(operatorId, reloaded.OperatorId);
        Assert.Equal(ConversationState.Assigned, reloaded.State);
        Assert.Equal(2, reloaded.LastSequence);
        Assert.Equal(Now, reloaded.CreatedAt);
        Assert.Equal(2, reloaded.Messages.Count);
        Assert.Equal("hello", reloaded.Messages.Single(m => m.Sequence == 1).Body.Value);
        Assert.Equal(MessageAuthorKind.Visitor, reloaded.Messages.Single(m => m.Sequence == 1).AuthorKind);
        Assert.Equal("hi there", reloaded.Messages.Single(m => m.Sequence == 2).Body.Value);
        Assert.Equal(MessageAuthorKind.Operator, reloaded.Messages.Single(m => m.Sequence == 2).AuthorKind);
    }

    [Fact]
    public async Task SaveThenMutateThenSaveAgain_PersistsTheNewMessageWithoutDuplicatingTheAggregate()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await SeedSiteAndVisitor(siteId, visitorId);

        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db1 = fixture.CreateDbContext())
        {
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("first"), Now);
            await new ConversationRepository(db1).SaveAsync(conversation, CancellationToken.None);
        }

        await using (var db2 = fixture.CreateDbContext())
        {
            var repository = new ConversationRepository(db2);
            var loaded = await repository.GetByIdAsync(conversationId, CancellationToken.None);
            loaded!.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("second"), Now);
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        await using var db3 = fixture.CreateDbContext();
        var final = await new ConversationRepository(db3).GetByIdAsync(conversationId, CancellationToken.None);
        Assert.Equal(2, final!.Messages.Count);
    }

    [Fact]
    public async Task GetActiveForVisitorAsync_IgnoresClosedConversations()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await SeedSiteAndVisitor(siteId, visitorId);

        var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        closed.Close(Now);
        await using (var db = fixture.CreateDbContext())
        {
            await new ConversationRepository(db).SaveAsync(closed, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var active = await new ConversationRepository(readDb).GetActiveForVisitorAsync(visitorId, CancellationToken.None);

        Assert.Null(active);
    }

    private async Task SeedSiteAndVisitor(SiteId siteId, VisitorId visitorId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        await db.SaveChangesAsync();
    }

    private async Task SeedSiteVisitorOperator(SiteId siteId, VisitorId visitorId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();
    }
}
