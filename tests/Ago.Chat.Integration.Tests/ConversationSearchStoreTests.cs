using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>`18-01`: the real-Postgres half of this item's own Done-when - "cross-site isolation is
/// proven by a test, not by the query looking right" (`17-01`'s own bar). Every test here seeds two
/// distinct sites through the ordinary domain path (`Conversation.AddVisitorMessage`, which stamps
/// `site_id` itself) and asserts the search never crosses the boundary, rather than trusting the SQL
/// by inspection.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationSearchStoreTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - 2-06 partitions messages by created_at, and only the current
    // month plus the next two ever have a partition in a fresh container (ConversationReadStoreTests'
    // own precedent).
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedConversationWithMessage(
        string body, DateTimeOffset createdAt)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var conversationId = await SeedConversationWithMessageOnSite(siteId, body, createdAt);
        return (siteId, conversationId);
    }

    /// <summary>Seeds onto an already-existing site (its own fresh <see cref="Visitor"/>, so two calls
    /// for the same <paramref name="siteId"/> never collide) - the shape
    /// <see cref="SearchAsync_ExcludesAMatchOutsideTheDateRange"/> needs to keep the date filter
    /// genuinely isolated from the site filter, rather than "outside the range" and "on a different
    /// site" being the same fact for every row under test.</summary>
    private async Task<ConversationId> SeedConversationWithMessageOnSite(SiteId siteId, string body, DateTimeOffset createdAt)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody(body), createdAt);

        await using var db = fixture.CreateDbContext();
        if (!await db.Sites.AnyAsync(s => s.Id == siteId))
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        }

        db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation.Id;
    }

    [Fact]
    public async Task SearchAsync_FindsAMessageContainingThePhrase()
    {
        var (siteId, conversationId) = await SeedConversationWithMessage("please refund my last order", Now);
        var store = new ConversationSearchStore(fixture.DataSource);

        var page = await store.SearchAsync(siteId, "refund", Now.AddDays(-1), Now.AddDays(1), null, 20, CancellationToken.None);

        var hit = Assert.Single(page.Results);
        Assert.Equal(conversationId, hit.ConversationId);
        Assert.Equal("please refund my last order", hit.MatchedBody);
    }

    [Fact]
    public async Task SearchAsync_WhenThePhraseDoesNotAppearInAnyMessage_ReturnsNoResults()
    {
        var (siteId, _) = await SeedConversationWithMessage("what are your opening hours", Now);
        var store = new ConversationSearchStore(fixture.DataSource);

        var page = await store.SearchAsync(siteId, "refund", Now.AddDays(-1), Now.AddDays(1), null, 20, CancellationToken.None);

        Assert.Empty(page.Results);
    }

    /// <summary>The item's own named bar: a matching message that genuinely exists, but on a
    /// different site, must never come back for this site's search - proven against the real query,
    /// not asserted about its shape.</summary>
    [Fact]
    public async Task SearchAsync_NeverReturnsAMatchFromADifferentSite()
    {
        var (siteId, _) = await SeedConversationWithMessage("please refund my last order", Now);
        var (_, otherConversationId) = await SeedConversationWithMessage("please refund my last order too", Now);
        var store = new ConversationSearchStore(fixture.DataSource);

        var page = await store.SearchAsync(siteId, "refund", Now.AddDays(-1), Now.AddDays(1), null, 20, CancellationToken.None);

        Assert.Single(page.Results);
        Assert.DoesNotContain(page.Results, r => r.ConversationId == otherConversationId);
    }

    /// <summary>`18-01`'s own bound decision made real: a message outside `[from, to)` is invisible to
    /// the search even though its site and its phrase both match - the date bound is a real filter,
    /// not a decoration on top of an otherwise-unscoped query.</summary>
    [Fact]
    public async Task SearchAsync_ExcludesAMatchOutsideTheDateRange()
    {
        var siteId = new SiteId(Guid.NewGuid());
        // Both messages on the same site, deliberately - the only thing that should separate them is
        // created_at, so this actually exercises the date filter rather than incidentally relying on
        // the site filter to do the excluding.
        var insideId = await SeedConversationWithMessageOnSite(siteId, "please refund my last order", Now.AddMinutes(-30));
        var outsideId = await SeedConversationWithMessageOnSite(siteId, "please refund my last order too", Now.AddHours(-3));
        var store = new ConversationSearchStore(fixture.DataSource);

        var page = await store.SearchAsync(
            siteId, "refund", Now.AddHours(-1), Now, null, 20, CancellationToken.None);

        var hit = Assert.Single(page.Results);
        Assert.Equal(insideId, hit.ConversationId);
        Assert.DoesNotContain(page.Results, r => r.ConversationId == outsideId);
    }

    [Fact]
    public async Task SearchAsync_PagesBackwardsById_WithNoGapsOrDuplicates()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var seenMessageIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
            var message = conversation.AddVisitorMessage(
                visitorId, new MessageId(Guid.NewGuid()), new MessageBody($"refund request {i}"), Now);

            await using var db = fixture.CreateDbContext();
            if (i == 0)
            {
                db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            }

            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
            seenMessageIds.Add(message.Id.Value);
        }

        var store = new ConversationSearchStore(fixture.DataSource);
        var pagedIds = new List<Guid>();
        Guid? cursor = null;
        do
        {
            var page = await store.SearchAsync(siteId, "refund", Now.AddDays(-1), Now.AddDays(1), cursor, 2, CancellationToken.None);
            pagedIds.AddRange(page.Results.Select(r => r.MessageId.Value));
            cursor = page.NextBeforeMessageId;
        } while (cursor is not null);

        Assert.Equal(seenMessageIds.OrderByDescending(id => id), pagedIds);
    }
}
