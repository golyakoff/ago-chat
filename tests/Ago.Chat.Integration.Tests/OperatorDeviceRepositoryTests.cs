using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-03`/`adr/0179` §1: proves both indexes `OperatorDeviceConfiguration` declares against a real
/// Postgres, not merely by reading the migration - a conflicting-row case for each, the same standard
/// this item's own Done-when asks for.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorDeviceRepositoryTests(PostgresFixture fixture)
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

    /// <summary>Every token below runs through this - `PostgresFixture` shares one real container
    /// (and one `operator_devices` table) across every test method in this whole collection, with no
    /// truncation between them, so a hardcoded literal like "token-1" reused across two facts collides
    /// on `ux_operator_devices_provider_token_active` the moment both happen to leave a live row
    /// behind. The same reason `SeedSiteAndOperatorAsync` above mints a fresh `Guid` for `SiteId`
    /// rather than a literal - found the hard way here, by two of this file's own facts colliding with
    /// each other on first run.</summary>
    private static string UniqueToken(string label) => $"{label}-{Guid.NewGuid():N}";

    [Fact]
    public async Task SaveAsync_ThenFindAsync_RoundTripsTheDevice()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var token = UniqueToken("token");
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
            token, Now);

        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorDeviceRepository(db).SaveAsync(device, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var loaded = await new OperatorDeviceRepository(readDb).FindAsync(operatorId, "installation-1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(token, loaded.Token);
        Assert.Equal(PushProvider.RuStore, loaded.Provider);
        Assert.Null(loaded.RevokedAt);
    }

    /// <summary>The row's own identity (`OperatorDevice`'s own remarks) - the storage-level backstop
    /// `RegisterOperatorDeviceHandler`'s upsert relies on to never produce two rows for one
    /// `(operatorId, installationId)`.</summary>
    [Fact]
    public async Task UniqueOperatorInstallationIndex_RefusesASecondRowForTheSamePair()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var first = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
                UniqueToken("token"), Now);
            await new OperatorDeviceRepository(db).SaveAsync(first, CancellationToken.None);
        }

        await using var conflictingDb = fixture.CreateDbContext();
        var second = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
            UniqueToken("token"), Now);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => new OperatorDeviceRepository(conflictingDb).SaveAsync(second, CancellationToken.None));
        Assert.Contains("ux_operator_devices_operator_installation", thrown.InnerException?.Message ?? thrown.Message);
    }

    /// <summary>`adr/0179` §1: a token must never be live on two rows - the restored-backup case this
    /// partial index exists to catch even when nothing in application code remembers to check first.</summary>
    [Fact]
    public async Task UniqueProviderTokenActiveIndex_RefusesASecondLiveRowForTheSameToken()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var sharedToken = UniqueToken("shared-token");

        await using (var db = fixture.CreateDbContext())
        {
            var first = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
                sharedToken, Now);
            await new OperatorDeviceRepository(db).SaveAsync(first, CancellationToken.None);
        }

        await using var conflictingDb = fixture.CreateDbContext();
        // A different installation (so the first unique index does not fire), same live token.
        var second = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-2", PushProvider.RuStore, "android",
            sharedToken, Now);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(
            () => new OperatorDeviceRepository(conflictingDb).SaveAsync(second, CancellationToken.None));
        Assert.Contains("ux_operator_devices_provider_token_active", thrown.InnerException?.Message ?? thrown.Message);
    }

    /// <summary>The index's own partial-ness, proven rather than assumed: once the first row is
    /// revoked, its token is free again - the exact "restored device backup" / reinstall case
    /// `adr/0179` names.</summary>
    [Fact]
    public async Task UniqueProviderTokenActiveIndex_AllowsTheSameTokenOnceTheFirstRowIsRevoked()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var sharedToken = UniqueToken("shared-token");

        await using (var db = fixture.CreateDbContext())
        {
            var first = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
                sharedToken, Now);
            first.Revoke(Now.AddHours(1));
            await new OperatorDeviceRepository(db).SaveAsync(first, CancellationToken.None);
        }

        await using var db2 = fixture.CreateDbContext();
        var second = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-2", PushProvider.RuStore, "android",
            sharedToken, Now.AddHours(2));

        // Must not throw.
        await new OperatorDeviceRepository(db2).SaveAsync(second, CancellationToken.None);
    }

    [Fact]
    public async Task FindActiveByTokenAsync_ReturnsNull_OnceTheHolderIsRevoked()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        var token = UniqueToken("token");
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-1", PushProvider.RuStore, "android",
            token, Now);

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new OperatorDeviceRepository(db);
            await repository.SaveAsync(device, CancellationToken.None);
            Assert.NotNull(await repository.FindActiveByTokenAsync(PushProvider.RuStore, token, CancellationToken.None));
        }

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new OperatorDeviceRepository(db);
            var loaded = await repository.FindAsync(operatorId, "installation-1", CancellationToken.None);
            loaded!.Revoke(Now.AddHours(1));
            await repository.SaveAsync(loaded, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        Assert.Null(
            await new OperatorDeviceRepository(readDb).FindActiveByTokenAsync(PushProvider.RuStore, token, CancellationToken.None));
    }

    /// <summary>`26-05`'s own future read, proven now (this item's own Done-when: "a revoked device is
    /// provably invisible to whatever `26-05` will later query").</summary>
    [Fact]
    public async Task ListActiveForOperatorAsync_ExcludesRevokedDevices()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new OperatorDeviceRepository(db);
            var live = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-live", PushProvider.RuStore,
                "android", UniqueToken("token-live"), Now);
            var revoked = OperatorDevice.Register(
                new OperatorDeviceId(Guid.NewGuid()), siteId, operatorId, "installation-revoked", PushProvider.RuStore,
                "android", UniqueToken("token-revoked"), Now);
            revoked.Revoke(Now.AddHours(1));

            await repository.SaveAsync(live, CancellationToken.None);
            await repository.SaveAsync(revoked, CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var active = await new OperatorDeviceRepository(readDb).ListActiveForOperatorAsync(operatorId, CancellationToken.None);

        var result = Assert.Single(active);
        Assert.Equal("installation-live", result.InstallationId);
    }
}
