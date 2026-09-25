using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-114`/`adr/0182`: <see cref="ConversationReadStore.GetVisitorSummaryAsync"/> against a real
/// Postgres - the header facts behind the contact-detail panel's "Первый визит {date} · N диалог(ов)".
/// </summary>
[Collection(PostgresCollection.Name)]
public class VisitorSummaryReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    private static readonly IIdGenerator IdGenerator = new UuidV7Generator();

    private async Task<(SiteId SiteId, VisitorId VisitorId, DateTimeOffset FirstSeenAt)> SeedSiteAndVisitor(DateTimeOffset firstSeenAt)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, firstSeenAt));
        await db.SaveChangesAsync();

        return (siteId, visitorId, firstSeenAt);
    }

    private async Task<Conversation> SeedClosedConversation(
        SiteId siteId, VisitorId visitorId, DateTimeOffset startedAt, DateTimeOffset closedAt)
    {
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(startedAt)), siteId, visitorId, startedAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), startedAt);
        conversation.Close(closedAt);

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation;
    }

    /// <summary>The item's own Done-when: a widget-only visitor (no `channel_identities` row is ever
    /// created in this test - `adr/0182` removed the gate that used to require one) with several
    /// conversations gets a count that includes the currently-open one, agreeing exactly with
    /// <see cref="ConversationReadStore.GetVisitorHistoryAsync"/>'s own (current-excluding) list length
    /// plus one - proven together against a real Postgres, not only against the in-memory fake.</summary>
    [Fact]
    public async Task GetVisitorSummaryAsync_ReturnsFirstSeenAtAndConversationCount_IncludingTheCurrentConversation_MatchingTheHistoryListPlusOne()
    {
        var (siteId, visitorId, firstSeenAt) = await SeedSiteAndVisitor(Now.AddDays(-30));
        var older = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10));
        var newer = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10));
        var current = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(current);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var summary = await store.GetVisitorSummaryAsync(visitorId, CancellationToken.None);
        var historyPage = await store.GetVisitorHistoryAsync(visitorId, current.Id, beforeId: null, pageSize: 50, CancellationToken.None);

        Assert.Equal(firstSeenAt, summary.FirstSeenAt);
        Assert.Equal(3, summary.ConversationCount);
        Assert.Equal(historyPage.Conversations.Count + 1, summary.ConversationCount);
        Assert.Equal([newer.Id, older.Id], historyPage.Conversations.Select(c => c.Id));
    }

    [Fact]
    public async Task GetVisitorSummaryAsync_ForAVisitorWithOnlyOneConversation_ReturnsCountOne()
    {
        var (siteId, visitorId, firstSeenAt) = await SeedSiteAndVisitor(Now);
        var only = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, visitorId, Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(only);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var summary = await store.GetVisitorSummaryAsync(visitorId, CancellationToken.None);

        Assert.Equal(1, summary.ConversationCount);
        Assert.Equal(firstSeenAt, summary.FirstSeenAt);
    }

    /// <summary>`24-10`: the identical exclusion <see cref="ConversationReadStore.GetVisitorHistoryAsync"/>
    /// already proves for the list (`VisitorHistoryReadStoreTests.GetVisitorHistoryAsync_ExcludesABlockedConversation`) -
    /// both queries must share the same predicate, or the count and the list could disagree about a
    /// blocked conversation specifically.</summary>
    [Fact]
    public async Task GetVisitorSummaryAsync_ExcludesABlockedConversation()
    {
        var (siteId, visitorId, _) = await SeedSiteAndVisitor(Now.AddDays(-1));
        await SeedClosedConversation(siteId, visitorId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10));
        var blocked = await SeedClosedConversation(siteId, visitorId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10));

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blocked.Id, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var summary = await store.GetVisitorSummaryAsync(visitorId, CancellationToken.None);

        Assert.Equal(1, summary.ConversationCount);
    }

    [Fact]
    public async Task GetVisitorSummaryAsync_NeverCountsAnotherVisitorsConversations()
    {
        var (siteId, visitorId, firstSeenAt) = await SeedSiteAndVisitor(Now);
        var (_, otherVisitorId, _) = await SeedSiteAndVisitor(Now.AddDays(-100));
        await SeedClosedConversation(siteId, visitorId, Now, Now.AddMinutes(10));

        await using (var db = fixture.CreateDbContext())
        {
            var theirs = Conversation.Start(new ConversationId(IdGenerator.NewId(Now)), siteId, otherVisitorId, Now);
            theirs.AddVisitorMessage(otherVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("theirs"), Now);
            db.Conversations.Add(theirs);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var summary = await store.GetVisitorSummaryAsync(visitorId, CancellationToken.None);

        Assert.Equal(1, summary.ConversationCount);
        Assert.Equal(firstSeenAt, summary.FirstSeenAt);
    }
}
