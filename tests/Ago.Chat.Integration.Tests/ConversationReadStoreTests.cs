using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class ConversationReadStoreTests(PostgresFixture fixture)
{
    // A fixed instant, truncated to whole seconds so it round-trips through Postgres's timestamptz
    // unchanged. No partition-boundary constraint to respect any more (15-09/adr/0087: messages is
    // PARTITION BY HASH (site_id), not RANGE (created_at)).
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(ConversationId ConversationId, SiteId SiteId)> SeedConversationWithMessages(int count)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        for (var i = 0; i < count; i++)
        {
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody($"message {i}"), Now);
        }

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (conversation.Id, siteId);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsMessagesNewestFirst()
    {
        var (conversationId, siteId) = await SeedConversationWithMessages(3);
        var store = new ConversationReadStore(fixture.DataSource);

        var page = await store.GetHistoryAsync(conversationId, siteId, beforeSequence: null, pageSize: 10, CancellationToken.None);

        Assert.Equal([3, 2, 1], page.Messages.Select(m => m.Sequence));
        Assert.Null(page.NextBeforeSequence);
    }

    [Fact]
    public async Task GetHistoryAsync_PagesBackwardsThroughTheFullHistoryWithNoGapsOrDuplicates()
    {
        var (conversationId, siteId) = await SeedConversationWithMessages(5);
        var store = new ConversationReadStore(fixture.DataSource);
        var seen = new List<int>();

        int? cursor = null;
        do
        {
            var page = await store.GetHistoryAsync(conversationId, siteId, cursor, pageSize: 2, CancellationToken.None);
            seen.AddRange(page.Messages.Select(m => m.Sequence));
            cursor = page.NextBeforeSequence;
        } while (cursor is not null);

        Assert.Equal([5, 4, 3, 2, 1], seen);
    }

    [Fact]
    public async Task GetHistoryAsync_WhenTheConversationHasNoMessages_ReturnsAnEmptyPage()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetHistoryAsync(conversation.Id, siteId, null, 10, CancellationToken.None);

        Assert.Empty(page.Messages);
        Assert.Null(page.NextBeforeSequence);
    }

    [Fact]
    public async Task GetDeltaAsync_ReturnsOnlyMessagesAfterTheGivenSequence_OldestFirst()
    {
        var (conversationId, siteId) = await SeedConversationWithMessages(5);
        var store = new ConversationReadStore(fixture.DataSource);

        var delta = await store.GetDeltaAsync(conversationId, siteId, afterSequence: 3, CancellationToken.None);

        Assert.Equal([4, 5], delta.Select(m => m.Sequence));
    }

    [Fact]
    public async Task GetDeltaAsync_WhenNothingIsNewerThanTheGivenSequence_ReturnsAnEmptyList()
    {
        var (conversationId, siteId) = await SeedConversationWithMessages(3);
        var store = new ConversationReadStore(fixture.DataSource);

        var delta = await store.GetDeltaAsync(conversationId, siteId, afterSequence: 3, CancellationToken.None);

        Assert.Empty(delta);
    }
}
