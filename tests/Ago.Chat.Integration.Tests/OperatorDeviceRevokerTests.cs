using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-03`/`adr/0179` §1: <see cref="OperatorDeviceRevoker"/> in isolation from the RabbitMQ/outbox
/// machinery around it - the same "prove the atomic claim on its own" shape
/// `OperatorConversationReleaserTests` already establishes for its own sibling class.
/// `OperatorRemovalEndToEndTests` proves the same behaviour again through the real
/// outbox/RabbitMQ/`OperatorRemovedConsumer` chain; this file proves the revoker's own logic directly.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperatorDeviceRevokerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();
        return (siteId, operatorId);
    }

    /// <summary>`PostgresFixture` shares one real container across every test method in this collection
    /// (and `OperatorDeviceRepositoryTests`' own) with no truncation between them, so a hardcoded token
    /// literal reused across two facts collides on `ux_operator_devices_provider_token_active` -
    /// `OperatorDeviceRepositoryTests`' own remarks on the identical bug, found there first.</summary>
    private static string UniqueToken(string label) => $"{label}-{Guid.NewGuid():N}";

    [Fact]
    public async Task RevokeAllAsync_RevokesEveryLiveDeviceForTheOperator()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var deviceIds = new List<OperatorDeviceId>();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new OperatorDeviceRepository(db);
            for (var i = 0; i < 3; i++)
            {
                var deviceId = new OperatorDeviceId(Guid.NewGuid());
                var device = OperatorDevice.Register(
                    deviceId, siteId, operatorId, $"installation-{i}", PushProvider.Fcm, "android", UniqueToken($"token-{i}"), Now);
                await repository.SaveAsync(device, CancellationToken.None);
                deviceIds.Add(deviceId);
            }
        }

        // A device belonging to a different operator entirely - must be left alone.
        var (otherSiteId, otherOperatorId) = await SeedSiteAndOperatorAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var other = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), otherSiteId, otherOperatorId, "installation-other", PushProvider.Fcm,
                "android", UniqueToken("token-other"), Now);
            await new OperatorDeviceRepository(db).SaveAsync(other, CancellationToken.None);
        }

        var revoker = new OperatorDeviceRevoker(fixture.DataSource, new SystemClock());
        var revoked = await revoker.RevokeAllAsync(operatorId, CancellationToken.None);

        Assert.Equal(3, revoked);

        await using var verify = fixture.CreateDbContext();
        foreach (var deviceId in deviceIds)
        {
            var device = await verify.OperatorDevices.FindAsync(deviceId);
            Assert.NotNull(device!.RevokedAt);
        }

        var untouched = await new OperatorDeviceRepository(verify).ListActiveForOperatorAsync(otherOperatorId, CancellationToken.None);
        Assert.Single(untouched);
    }

    [Fact]
    public async Task RevokeAllAsync_WhenOperatorHasNoLiveDevices_ReturnsZero()
    {
        var (_, operatorId) = await SeedSiteAndOperatorAsync();

        var revoker = new OperatorDeviceRevoker(fixture.DataSource, new SystemClock());
        var revoked = await revoker.RevokeAllAsync(operatorId, CancellationToken.None);

        Assert.Equal(0, revoked);
    }

    /// <summary>`adr/0179`: idempotent - a redelivered `OperatorRemovedFromSite` must revoke an
    /// already-revoked row harmlessly, not throw or double-count.</summary>
    [Fact]
    public async Task RevokeAllAsync_CalledTwice_IsIdempotent()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var device = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.Fcm, "android",
                UniqueToken("token"), Now);
            await new OperatorDeviceRepository(db).SaveAsync(device, CancellationToken.None);
        }

        var revoker = new OperatorDeviceRevoker(fixture.DataSource, new SystemClock());

        Assert.Equal(1, await revoker.RevokeAllAsync(operatorId, CancellationToken.None));
        Assert.Equal(0, await revoker.RevokeAllAsync(operatorId, CancellationToken.None));
    }
}
