using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>`23-19`'s own scope: "its own window and its own prune job" - <c>WebhookDeliveryPruneJobTests</c>'
/// own shape, applied to <c>channel_deliveries</c>. Real Postgres, real FKs to real `sites`/
/// `channel_identities` rows (`testing.md`).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelDeliveryPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    [Fact]
    public async Task PruneAsync_RemovesADelivery_OlderThanTheRetentionWindow()
    {
        var id = await SeedDeliveryAsync(attemptedAt: Now - RetentionWindow - TimeSpan.FromDays(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await DeliveryExistsAsync(id));
    }

    [Fact]
    public async Task PruneAsync_LeavesADelivery_YoungerThanTheRetentionWindowAlone()
    {
        var id = await SeedDeliveryAsync(attemptedAt: Now - TimeSpan.FromDays(1));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);
            Assert.True(await DeliveryExistsAsync(id));
        }
        finally
        {
            await DeleteDeliveryAsync(id);
        }
    }

    /// <summary>"Yesterday's failure" - `6-03`'s own phrase, reused verbatim in
    /// `ChannelDeliveryPruneJobOptions`' own remarks - is the floor this window must clear for a
    /// refused channel send too.</summary>
    [Fact]
    public async Task PruneAsync_LeavesYesterdaysRefusalVisible()
    {
        var id = await SeedDeliveryAsync(attemptedAt: Now - TimeSpan.FromDays(1), status: ChannelDeliveryStatus.Refused);

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);
            Assert.True(await DeliveryExistsAsync(id));
        }
        finally
        {
            await DeleteDeliveryAsync(id);
        }
    }

    private ChannelDeliveryPruneJob CreateJob() =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new ChannelDeliveryPruneJobOptions { RetentionWindow = RetentionWindow }),
            NullLogger<ChannelDeliveryPruneJob>.Instance);

    private async Task<Guid> SeedDeliveryAsync(
        DateTimeOffset attemptedAt, ChannelDeliveryStatus status = ChannelDeliveryStatus.Delivered)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), siteId, ChannelKind.Sms,
            new ExternalChannelAddress($"+7{Random.Shared.NextInt64(1_000_000_000, 9_999_999_999)}"), visitorId, attemptedAt);
        var deliveryId = new ChannelDeliveryId(Guid.NewGuid());
        var delivery = ChannelDelivery.Record(
            deliveryId, siteId, new ConversationId(Guid.NewGuid()), new MessageId(Guid.NewGuid()), ChannelKind.Sms, identity.Id,
            status, providerMessageId: status == ChannelDeliveryStatus.Delivered ? "sms-1" : null,
            failureReason: status == ChannelDeliveryStatus.Refused ? "wrong number" : null, attemptedAt);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, attemptedAt));
        db.ChannelIdentities.Add(identity);
        db.ChannelDeliveries.Add(delivery);
        await db.SaveChangesAsync(CancellationToken.None);
        return deliveryId.Value;
    }

    private async Task<bool> DeliveryExistsAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ChannelDeliveries.AnyAsync(d => d.Id == new ChannelDeliveryId(id), CancellationToken.None);
    }

    private async Task DeleteDeliveryAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.ChannelDeliveries.SingleOrDefaultAsync(d => d.Id == new ChannelDeliveryId(id), CancellationToken.None);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
