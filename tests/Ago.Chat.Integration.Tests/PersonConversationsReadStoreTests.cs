using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-269`: <see cref="ConversationReadStore.GetConversationsForPersonAsync"/> against a real
/// Postgres - the client-detail hub's own "which dialog do I open for this person" read, mirroring
/// <c>VisitorHistoryReadStoreTests</c>'s own real-uuid-v7, real-Postgres shape.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PersonConversationsReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    // Real uuid v7 ids, not Guid.NewGuid() - the query's own tiebreaker for two conversations in the
    // same "active-or-not" bucket is `id desc`, the same "conversation ids are uuid v7, so id order is
    // already creation order" reasoning `VisitorHistoryReadStoreTests` already states for itself.
    private static readonly IIdGenerator IdGenerator = new UuidV7Generator();

    private async Task<(SiteId SiteId, VisitorId PersonId)> SeedSiteAndPerson()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var personId = new VisitorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(personId, siteId, Now));
        await db.SaveChangesAsync();

        return (siteId, personId);
    }

    private async Task<Conversation> SeedClosedConversation(
        SiteId siteId, VisitorId personId, DateTimeOffset startedAt, DateTimeOffset closedAt, string lastMessageBody)
    {
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(startedAt)), siteId, personId, startedAt);
        conversation.AddVisitorMessage(personId, new MessageId(Guid.NewGuid()), new MessageBody(lastMessageBody), startedAt);
        conversation.Close(closedAt);

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation;
    }

    private async Task<Conversation> SeedActiveConversation(
        SiteId siteId, VisitorId personId, DateTimeOffset startedAt, string firstMessageBody)
    {
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(startedAt)), siteId, personId, startedAt);
        conversation.AddVisitorMessage(personId, new MessageId(Guid.NewGuid()), new MessageBody(firstMessageBody), startedAt);

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation;
    }

    [Fact]
    public async Task GetConversationsForPersonAsync_ForAPersonWithNoConversations_ReturnsAnEmptyList()
    {
        var (_, personId) = await SeedSiteAndPerson();

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task GetConversationsForPersonAsync_TheActiveConversationSortsFirst_EvenWhenOlderThanClosedOnes()
    {
        var (siteId, personId) = await SeedSiteAndPerson();
        var closed = await SeedClosedConversation(siteId, personId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "closed: resolved");
        // Started well before the closed one, but never closed - it must still sort first.
        var active = await SeedActiveConversation(siteId, personId, Now.AddDays(-10), "active: still going");

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        Assert.Equal([active.Id, closed.Id], items.Select(i => i.Id));
        Assert.Equal(nameof(ConversationState.Waiting), items[0].State);
        Assert.Null(items[0].ClosedAt);
        Assert.Equal(nameof(ConversationState.Closed), items[1].State);
        Assert.NotNull(items[1].ClosedAt);
    }

    [Fact]
    public async Task GetConversationsForPersonAsync_WithNoActiveConversation_TheMostRecentClosedOneSortsFirst()
    {
        var (siteId, personId) = await SeedSiteAndPerson();
        var older = await SeedClosedConversation(siteId, personId, Now.AddDays(-5), Now.AddDays(-5).AddMinutes(10), "older");
        var newer = await SeedClosedConversation(siteId, personId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "newer");

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        Assert.Equal([newer.Id, older.Id], items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetConversationsForPersonAsync_ForAConversationWithNoMessagesYet_FallsBackToItsOwnStartTime()
    {
        var (siteId, personId) = await SeedSiteAndPerson();
        var startedAt = Now.AddHours(-1);
        var conversation = Conversation.Start(new ConversationId(IdGenerator.NewId(startedAt)), siteId, personId, startedAt);
        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(conversation.Id, item.Id);
        Assert.Equal(startedAt, item.LastActivityAt);
    }

    /// <summary>The identical "unreachable, not merely hidden" rule `VisitorHistoryReadStoreTests`
    /// already proves for the sibling visitor-scoped read.</summary>
    [Fact]
    public async Task GetConversationsForPersonAsync_ExcludesABlockedConversation()
    {
        var (siteId, personId) = await SeedSiteAndPerson();
        var kept = await SeedClosedConversation(siteId, personId, Now.AddDays(-2), Now.AddDays(-2).AddMinutes(10), "kept");
        var blocked = await SeedClosedConversation(siteId, personId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "blocked");

        var blocks = new ConversationBlockRepository(fixture.DataSource);
        var outcome = await blocks.BlockAsync(
            blocked.Id, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        Assert.Equal(ConversationBlockOutcome.Applied, outcome);

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        Assert.Equal([kept.Id], items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetConversationsForPersonAsync_NeverReturnsAnotherPersonsConversations()
    {
        var (siteId, personId) = await SeedSiteAndPerson();
        var (_, otherPersonId) = await SeedSiteAndPerson();
        var mine = await SeedClosedConversation(siteId, personId, Now.AddDays(-1), Now.AddDays(-1).AddMinutes(10), "mine");
        await SeedClosedConversation(siteId, otherPersonId, Now, Now.AddMinutes(10), "theirs");

        var store = new ConversationReadStore(fixture.DataSource);
        var items = await store.GetConversationsForPersonAsync(personId, CancellationToken.None);

        Assert.Equal([mine.Id], items.Select(i => i.Id));
    }
}
