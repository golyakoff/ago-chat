using Ago.Chat.Application.Abstractions;
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

    // `24-10`: "the conversation detail" - GetConversationByIdHandler's own single-conversation fetch
    // reads through this exact method. A blocked conversation must read exactly like one that does not
    // exist, the same not-found-shaped hiding every operator-facing read in this codebase now gives a
    // blocked conversation.
    [Fact]
    public async Task GetByIdAsync_ExcludesABlockedConversation()
    {
        var (conversationId, siteId) = await SeedConversationWithMessages(1);
        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var item = await store.GetByIdAsync(conversationId, siteId, CancellationToken.None);

        Assert.Null(item);
    }

    // `24-10`: "the conversation list" - the site-wide admin/supervisor read. One site, two
    // conversations: the blocked one must be absent and the ordinary one must still come through, not
    // merely "the list is missing something" but specifically the right thing.
    [Fact]
    public async Task GetAllForSiteAsync_ExcludesABlockedConversation_ButKeepsTheOrdinaryOne()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var keptVisitorId = new VisitorId(Guid.NewGuid());
        var blockedVisitorId = new VisitorId(Guid.NewGuid());
        var kept = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, keptVisitorId, Now);
        var blocked = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, blockedVisitorId, Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(keptVisitorId, siteId, Now));
            db.Visitors.Add(new Visitor(blockedVisitorId, siteId, Now));
            db.Conversations.Add(kept);
            db.Conversations.Add(blocked);
            await db.SaveChangesAsync();
        }

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blocked.Id, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetAllForSiteAsync(siteId, beforeId: null, pageSize: 50, tagId: null, CancellationToken.None);

        Assert.Equal([kept.Id], page.Conversations.Select(c => c.Id));
    }

    // `25-56`'s own second half: proves the real `left join lateral` against `visitor_contact_details`
    // in `AllForSiteSql`/`ByIdSql` actually runs against Postgres, not only the hand-mirrored logic
    // `FakeConversationReadStore` gives GetAllConversationsForSiteHandlerTests. Two Name-kind rows for
    // the same visitor, seeded out of order, prove the lateral's own `order by recorded_at desc limit 1`
    // picks the most recent one, not the last one inserted.
    [Fact]
    public async Task GetByIdAsync_AVisitorWithTwoNameRows_ReturnsTheMostRecentlyRecordedOne()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            // Seeded out of RecordedAt order on purpose - the lateral's own `order by recorded_at desc
            // limit 1` must pick the most recent one by that column, not by insertion order.
            db.VisitorContactDetails.Add(VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Name, "Иван",
                Now.AddMinutes(-10)));
            db.VisitorContactDetails.Add(VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(Guid.NewGuid()), visitorId, VisitorContactDetailKind.Name, "Иван Иванов",
                Now));
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var item = await store.GetByIdAsync(conversation.Id, siteId, CancellationToken.None);

        Assert.Equal("Иван Иванов", item?.VisitorName);
    }

    // The ordinary case - most visitors never give a name at all, and this must not be confused with
    // one that simply has no rows yet (no exception, no placeholder string).
    [Fact]
    public async Task GetAllForSiteAsync_AVisitorWithNoNameRow_ReturnsANullVisitorName()
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
        var page = await store.GetAllForSiteAsync(siteId, beforeId: null, pageSize: 50, tagId: null, CancellationToken.None);

        Assert.Null(Assert.Single(page.Conversations).VisitorName);
    }
}
