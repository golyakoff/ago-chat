using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-07`: <see cref="ConversationReadStore.GetVisitorHistoryAsync"/> against a real Postgres -
/// migrations applied from scratch (`PostgresFixture`), so this is also the first real proof that
/// `Stage18AddConversationClosedAtAndVisitorHistoryIndexes` runs clean.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VisitorHistoryReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    // Real uuid v7 ids, not Guid.NewGuid() - GetVisitorHistoryAsync orders by `id desc` (the same
    // "conversation ids are uuid v7, so id order is already creation order" reasoning
    // ConversationListPage's own remarks give for the site-wide list), so the "newest first" claim
    // this test makes is only meaningful against ids that are actually time-ordered the way
    // production's IIdGenerator produces them.
    private static readonly IIdGenerator IdGenerator = new UuidV7Generator();

    private async Task<(SiteId SiteId, VisitorId VisitorId)> SeedSiteAndVisitor()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        await db.SaveChangesAsync();

        return (siteId, visitorId);
    }

    private async Task<Conversation> SeedClosedConversation(
        SiteId siteId, VisitorId visitorId, DateTimeOffset startedAt, DateTimeOffset closedAt, string lastMessageBody)
    {
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(startedAt)), siteId, visitorId, startedAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody(lastMessageBody), startedAt);
        conversation.Close(closedAt);

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation;
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_ReturnsThisVisitorsOtherConversations_NewestFirst_WithTheLastMessageAsThePreview()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var older = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10), "older: how do I return this?");
        var newer = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "newer: thanks, resolved");
        var current = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(current);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, current.Id, beforeId: null, pageSize: 10, CancellationToken.None);

        Assert.Equal([newer.Id, older.Id], page.Conversations.Select(c => c.Id));
        Assert.DoesNotContain(page.Conversations, c => c.Id == current.Id);
        Assert.Null(page.NextBeforeId);

        var newerItem = page.Conversations.Single(c => c.Id == newer.Id);
        Assert.Equal(nameof(ConversationState.Closed), newerItem.State);
        Assert.NotNull(newerItem.ClosedAt);
        Assert.Equal("newer: thanks, resolved", newerItem.PreviewBody);
        Assert.Equal(MessageAuthorKind.Visitor, newerItem.PreviewAuthorKind);
    }

    /// <summary>`24-10`: one of this item's own named read paths - "18-07's cross-conversation visitor
    /// history" - excludes a blocked conversation the same way the site-wide list does. The visitor's
    /// other, unblocked conversation still comes through.</summary>
    [Fact]
    public async Task GetVisitorHistoryAsync_ExcludesABlockedConversation()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var kept = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10), "kept: how do I return this?");
        var blocked = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "blocked: should not surface");
        var current = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(current);
            await db.SaveChangesAsync();
        }

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blocked.Id, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, current.Id, beforeId: null, pageSize: 10, CancellationToken.None);

        Assert.Equal([kept.Id], page.Conversations.Select(c => c.Id));
    }

    /// <summary>`24-10`: `ListAllForVisitorAsync` backs `ExportVisitorHandler` (`24-11`) - a blocked
    /// conversation must not ride along inside a visitor-scoped export of that same visitor's *other*
    /// conversations (the named conversation's own block state is checked one level up, at
    /// `GetByIdAsync` - `ConversationReadStoreTests.GetByIdAsync_ExcludesABlockedConversation`'s own
    /// remarks).</summary>
    [Fact]
    public async Task ListAllForVisitorAsync_ExcludesABlockedConversation()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var kept = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10), "kept");
        var blocked = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "blocked");

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blocked.Id, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var ids = await store.ListAllForVisitorAsync(visitorId, CancellationToken.None);

        Assert.Equal([kept.Id], ids);
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_ForAConversationWithSeveralMessages_PreviewsTheLastOne_NotTheFirst()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(Now.AddDays(-1))), siteId, visitorId, Now.AddDays(-1));
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("first: hello"), Now.AddDays(-1));
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("last: goodbye"), Now.AddDays(-1).AddMinutes(5));
        conversation.Close(Now.AddDays(-1).AddMinutes(10));
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var excludeId = new ConversationId(IdGenerator.NewId(Now));
        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, excludeId, beforeId: null, pageSize: 10, CancellationToken.None);

        var item = Assert.Single(page.Conversations);
        Assert.Equal("last: goodbye", item.PreviewBody);
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_ExcludesTheCurrentConversation_EvenWhenItIsTheOnlyOtherOneForThisVisitor()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var current = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(current);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, current.Id, beforeId: null, pageSize: 10, CancellationToken.None);

        Assert.Empty(page.Conversations);
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_PagesBackwardsThroughTheFullHistoryWithNoGapsOrDuplicates()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var seeded = new List<ConversationId>();
        for (var i = 0; i < 5; i++)
        {
            var conversation = await SeedClosedConversation(
                siteId, visitorId, Now.AddDays(-5 + i), Now.AddDays(-5 + i).AddMinutes(10), $"message {i}");
            seeded.Add(conversation.Id);
        }

        var excludeId = new ConversationId(IdGenerator.NewId(Now.AddDays(1)));
        var store = new ConversationReadStore(fixture.DataSource);
        var seen = new List<ConversationId>();

        Guid? cursor = null;
        do
        {
            var page = await store.GetVisitorHistoryAsync(visitorId, excludeId, cursor, pageSize: 2, CancellationToken.None);
            seen.AddRange(page.Conversations.Select(c => c.Id));
            cursor = page.NextBeforeId;
        } while (cursor is not null);

        Assert.Equal(Enumerable.Reverse(seeded), seen);
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_NeverReturnsAnotherVisitorsConversations()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var (_, otherVisitorId) = await SeedSiteAndVisitor();
        var mine = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "mine");

        await using (var db = fixture.CreateDbContext())
        {
            var theirs = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, otherVisitorId, Now);
            theirs.AddVisitorMessage(otherVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("theirs"), Now);
            theirs.Close(Now.AddMinutes(10));
            db.Conversations.Add(theirs);
            await db.SaveChangesAsync();
        }

        var excludeId = new ConversationId(IdGenerator.NewId(Now.AddDays(1)));
        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, excludeId, beforeId: null, pageSize: 10, CancellationToken.None);

        Assert.Equal([mine.Id], page.Conversations.Select(c => c.Id));
    }

    [Fact]
    public async Task GetVisitorHistoryAsync_ForAConversationWithNoMessages_ReturnsItWithNoPreview()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitor();
        var noMessages = Conversation.Start(new ConversationId(IdGenerator.NewId(Now.AddDays(-1))), siteId, visitorId, Now.AddDays(-1));
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(noMessages);
            await db.SaveChangesAsync();
        }

        var excludeId = new ConversationId(IdGenerator.NewId(Now));
        var store = new ConversationReadStore(fixture.DataSource);
        var page = await store.GetVisitorHistoryAsync(visitorId, excludeId, beforeId: null, pageSize: 10, CancellationToken.None);

        var item = Assert.Single(page.Conversations);
        Assert.Equal(noMessages.Id, item.Id);
        Assert.Null(item.PreviewBody);
        Assert.Null(item.PreviewAuthorKind);
        Assert.Null(item.PreviewCreatedAt);
        Assert.Null(item.ClosedAt);
    }
}
