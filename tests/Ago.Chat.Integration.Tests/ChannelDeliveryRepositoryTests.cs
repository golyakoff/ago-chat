using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-19`: the storage-level half of the idempotency claim - `ChannelDeliveryHandlerTests` (Application)
/// proves the handler calls <c>SaveAsync</c> once per outcome; this proves the unique index on
/// <c>message_id</c> is what actually stops a second row landing when two processes (or two attempts
/// against the same DbContext-free repository instance) race the same insert, the same division
/// `ChannelIdentityPersistenceTests`' own remarks draw between the primary mechanism and its backstop.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChannelDeliveryRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId Site, ChannelIdentityId Identity, ConversationId Conversation)> SeedSiteAndIdentityAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
            new ExternalChannelAddress($"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}"), visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.ChannelIdentities.Add(identity);
        await db.SaveChangesAsync();

        return (siteId, identity.Id, new ConversationId(Guid.NewGuid()));
    }

    [Fact]
    public async Task SaveAsync_ADeliveredOutcome_RoundTrips()
    {
        var (siteId, identityId, conversationId) = await SeedSiteAndIdentityAsync();
        var messageId = new MessageId(Guid.NewGuid());
        var delivery = ChannelDelivery.Record(
            new ChannelDeliveryId(Guid.NewGuid()), siteId, conversationId, messageId, ChannelKind.Sms, identityId,
            ChannelDeliveryStatus.Delivered, providerMessageId: "sms-1", failureReason: null, Now);

        await using (var db = fixture.CreateDbContext())
        {
            var saved = await new ChannelDeliveryRepository(db).SaveAsync(delivery, CancellationToken.None);
            Assert.True(saved);
        }

        await using var readDb = fixture.CreateDbContext();
        var found = await readDb.ChannelDeliveries.SingleAsync(d => d.MessageId == messageId, CancellationToken.None);
        Assert.Equal(ChannelDeliveryStatus.Delivered, found.Status);
        Assert.Equal("sms-1", found.ProviderMessageId);
        Assert.Equal(identityId, found.ChannelIdentityId);
    }

    /// <summary>The item's own Done-when: "a redelivered broker message does not write a second row" -
    /// proven here at the level that actually enforces it, the unique index on <c>message_id</c>, using
    /// two independent <c>DbContext</c>s the way two separate consumer attempts would.</summary>
    [Fact]
    public async Task SaveAsync_CalledTwiceForTheSameMessageId_TheSecondCallIsANoOp()
    {
        var (siteId, identityId, conversationId) = await SeedSiteAndIdentityAsync();
        var messageId = new MessageId(Guid.NewGuid());

        bool firstSaved;
        await using (var db = fixture.CreateDbContext())
        {
            firstSaved = await new ChannelDeliveryRepository(db).SaveAsync(
                ChannelDelivery.Record(
                    new ChannelDeliveryId(Guid.NewGuid()), siteId, conversationId, messageId, ChannelKind.Sms, identityId,
                    ChannelDeliveryStatus.Refused, providerMessageId: null, failureReason: "wrong number", Now),
                CancellationToken.None);
        }

        bool secondSaved;
        await using (var db = fixture.CreateDbContext())
        {
            // A second, independent attempt - a different ChannelDeliveryId, as a fresh consumer
            // attempt would generate - racing the same MessageId.
            secondSaved = await new ChannelDeliveryRepository(db).SaveAsync(
                ChannelDelivery.Record(
                    new ChannelDeliveryId(Guid.NewGuid()), siteId, conversationId, messageId, ChannelKind.Sms, identityId,
                    ChannelDeliveryStatus.Delivered, providerMessageId: "sms-2", failureReason: null, Now),
                CancellationToken.None);
        }

        Assert.True(firstSaved);
        Assert.False(secondSaved);

        await using var readDb = fixture.CreateDbContext();
        var rows = await readDb.ChannelDeliveries.Where(d => d.MessageId == messageId).ToListAsync(CancellationToken.None);
        var row = Assert.Single(rows);
        // The first attempt's own outcome, not the second's - the second never landed.
        Assert.Equal(ChannelDeliveryStatus.Refused, row.Status);
    }

    /// <summary>The address-versus-reference decision's own cascade claim: a whole-site erasure
    /// (`SiteErasureQuery.DeleteSiteAsync`'s `DELETE FROM sites`) removes this table's rows too, via the
    /// direct `site_id` foreign key - no orphaned delivery record survives the site it is about.</summary>
    [Fact]
    public async Task DeletingTheSite_CascadesToItsChannelDeliveries()
    {
        var (siteId, identityId, conversationId) = await SeedSiteAndIdentityAsync();
        var messageId = new MessageId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            await new ChannelDeliveryRepository(db).SaveAsync(
                ChannelDelivery.Record(
                    new ChannelDeliveryId(Guid.NewGuid()), siteId, conversationId, messageId, ChannelKind.Sms, identityId,
                    ChannelDeliveryStatus.Delivered, providerMessageId: "sms-3", failureReason: null, Now),
                CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await db.Sites.Where(s => s.Id == siteId).ExecuteDeleteAsync(CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        Assert.False(await readDb.ChannelDeliveries.AnyAsync(d => d.MessageId == messageId, CancellationToken.None));
    }
}
