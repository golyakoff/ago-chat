using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class ChannelDeliveryReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId Site, ChannelIdentityId Identity)> SeedSiteAndIdentityAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            new ExternalChannelAddress($"tg-{Guid.NewGuid():N}"), visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.ChannelIdentities.Add(identity);
        await db.SaveChangesAsync();

        return (siteId, identity.Id);
    }

    private async Task SeedDeliveryAsync(
        SiteId siteId, ConversationId conversationId, ChannelIdentityId identityId, DateTimeOffset attemptedAt,
        ChannelDeliveryStatus status)
    {
        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), siteId, conversationId, new MessageId(Guid.NewGuid()), ChannelKind.Telegram,
            identityId, status, providerMessageId: status == ChannelDeliveryStatus.Delivered ? "tg-msg" : null,
            failureReason: status == ChannelDeliveryStatus.Refused ? "blocked" : null, attemptedAt);

        await using var db = fixture.CreateDbContext();
        await new ChannelDeliveryRepository(db).SaveAsync(delivery, CancellationToken.None);
    }

    [Fact]
    public async Task GetForConversationAsync_ReturnsDeliveriesNewestFirst()
    {
        var (siteId, identityId) = await SeedSiteAndIdentityAsync();
        var conversationId = new ConversationId(Guid.NewGuid());
        await SeedDeliveryAsync(siteId, conversationId, identityId, Now, ChannelDeliveryStatus.Delivered);
        await SeedDeliveryAsync(siteId, conversationId, identityId, Now.AddSeconds(1), ChannelDeliveryStatus.Refused);

        var readStore = new ChannelDeliveryReadStore(fixture.DataSource);
        var items = await readStore.GetForConversationAsync(conversationId, siteId, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(ChannelDeliveryStatus.Refused, items[0].Status); // Now+1, newest
        Assert.Equal(ChannelDeliveryStatus.Delivered, items[1].Status); // Now, oldest
    }

    /// <summary>The item's own Done-when: "another tenant's delivery records cannot be read" - proven
    /// at the read-store level, which is the second, independent line behind the handler's own
    /// assigned-operator gate (`ChannelDeliveryReadStore`'s own remarks).</summary>
    [Fact]
    public async Task GetForConversationAsync_NeverReturnsAnotherSitesDeliveries()
    {
        var (siteId, identityId) = await SeedSiteAndIdentityAsync();
        var (otherSiteId, otherIdentityId) = await SeedSiteAndIdentityAsync();
        var conversationId = new ConversationId(Guid.NewGuid());
        await SeedDeliveryAsync(siteId, conversationId, identityId, Now, ChannelDeliveryStatus.Delivered);
        // Same conversation id reused across sites is not realistic in production (ids are globally
        // unique), but proves the read store's own site_id filter is load-bearing rather than
        // incidental - a conversation_id-only query would leak this row across the tenant boundary.
        await SeedDeliveryAsync(otherSiteId, conversationId, otherIdentityId, Now, ChannelDeliveryStatus.Delivered);

        var readStore = new ChannelDeliveryReadStore(fixture.DataSource);
        var items = await readStore.GetForConversationAsync(conversationId, siteId, CancellationToken.None);

        Assert.Single(items);
    }

    [Fact]
    public async Task GetForConversationAsync_ForAConversationWithNoDeliveries_ReturnsEmpty()
    {
        var (siteId, _) = await SeedSiteAndIdentityAsync();

        var readStore = new ChannelDeliveryReadStore(fixture.DataSource);
        var items = await readStore.GetForConversationAsync(new ConversationId(Guid.NewGuid()), siteId, CancellationToken.None);

        Assert.Empty(items);
    }
}
