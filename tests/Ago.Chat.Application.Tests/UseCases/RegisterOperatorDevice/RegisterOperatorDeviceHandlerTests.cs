using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RegisterOperatorDevice;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RegisterOperatorDevice;

public class RegisterOperatorDeviceHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RegisterOperatorDeviceHandler Handler, FakeOperatorDeviceRepository Devices, FakeClock Clock);

    private static Fixture CreateFixture()
    {
        var devices = new FakeOperatorDeviceRepository();
        var clock = new FakeClock(Now);
        var handler = new RegisterOperatorDeviceHandler(devices, new FakeIdGenerator(), clock);
        return new Fixture(handler, devices, clock);
    }

    [Fact]
    public async Task HandleAsync_FirstCallForAnInstallation_CreatesOneDevice()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("token-1", saved.Token);
        Assert.Null(saved.RevokedAt);
    }

    /// <summary>This item's own Done-when, stated as its own test: calling the route twice with the
    /// same `installationId` and a new token updates the one existing row, never inserts a second
    /// one.</summary>
    [Fact]
    public async Task HandleAsync_CalledTwiceWithTheSameInstallationId_UpdatesTheOneRow_NeverInsertsASecond()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-1"),
            CancellationToken.None);

        fixture.Clock.UtcNow = Now.AddDays(1);
        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-2"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var all = await fixture.Devices.ListActiveForOperatorAsync(OperatorId, CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal("token-2", only.Token);
        Assert.Equal(Now.AddDays(1), only.LastSeenAt);
    }

    [Fact]
    public async Task HandleAsync_OnARevokedInstallation_RevivesIt()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-1"),
            CancellationToken.None);
        var device = await fixture.Devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        device!.Revoke(Now.AddHours(1));
        await fixture.Devices.SaveAsync(device, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-2"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = await fixture.Devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        Assert.Null(reloaded!.RevokedAt);
    }

    /// <summary>`adr/0179` §1, step 1: the restored-backup case - registering a token still live on a
    /// different installation revokes that other row first.</summary>
    [Fact]
    public async Task HandleAsync_WhenAnotherInstallationHoldsTheSameLiveToken_RevokesTheOtherRow()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-old", PushProvider.RuStore, "android", "shared-token"),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-new", PushProvider.RuStore, "android", "shared-token"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var old = await fixture.Devices.FindAsync(OperatorId, "installation-old", CancellationToken.None);
        var fresh = await fixture.Devices.FindAsync(OperatorId, "installation-new", CancellationToken.None);
        Assert.NotNull(old!.RevokedAt);
        Assert.Null(fresh!.RevokedAt);
    }

    /// <summary>
    /// `26-122`'s own Done-when, stated as its own test: a reinstall regenerates
    /// `installationId` (`DataStoreInstallationId`'s own doc comment), but the stable `deviceId`
    /// travels with it - so the second call must replace the first row rather than add a second, which
    /// is exactly the `26-83` bug (one operator, five stale rows) this item exists to remove.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ReinstallWithANewInstallationIdButTheSameDeviceId_ReplacesTheRow()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-before-reinstall", PushProvider.RuStore, "android", "token-1",
                "device-stable"),
            CancellationToken.None);

        fixture.Clock.UtcNow = Now.AddDays(1);
        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-after-reinstall", PushProvider.RuStore, "android", "token-2",
                "device-stable"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var all = await fixture.Devices.ListActiveForOperatorAsync(OperatorId, CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal("token-2", only.Token);
        Assert.Equal("installation-after-reinstall", only.InstallationId);
        Assert.Equal("device-stable", only.DeviceId);
    }

    /// <summary>`26-100`'s own transport switch (RuStore-&gt;FCM), subsumed by `26-122`'s device-keyed
    /// upsert per the backlog's own "fix C folded into A": a re-registration keyed on the same device
    /// replaces the row regardless of which provider it now carries, with no separate revoke-on-switch
    /// mechanism needed.</summary>
    [Fact]
    public async Task HandleAsync_SameDeviceSwitchingProvider_ReplacesTheRowRatherThanAddingASecond()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "rustore-token", "device-stable"),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.Fcm, "android", "fcm-token", "device-stable"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var all = await fixture.Devices.ListActiveForOperatorAsync(OperatorId, CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal(PushProvider.Fcm, only.Provider);
        Assert.Equal("fcm-token", only.Token);
    }

    /// <summary>A row written before this item's own migration has `DeviceId == null` - the very next
    /// registration from the same install, now carrying a real device id, must adopt that row (backfill
    /// its `DeviceId`) rather than fail the unique installation index with a second insert.</summary>
    [Fact]
    public async Task HandleAsync_APreExistingRowWithNoDeviceId_IsAdoptedAndBackfilled()
    {
        var fixture = CreateFixture();
        // Simulates a row this table already held before `26-122`'s migration - registered with no
        // device id at all, the only shape a pre-migration row can have.
        await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-1"),
            CancellationToken.None);
        var preMigration = await fixture.Devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        Assert.Null(preMigration!.DeviceId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-2", "device-stable"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var all = await fixture.Devices.ListActiveForOperatorAsync(OperatorId, CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal(preMigration.Id, only.Id);
        Assert.Equal("device-stable", only.DeviceId);
        Assert.Equal("token-2", only.Token);
    }

    [Fact]
    public async Task HandleAsync_WithABlankToken_ReturnsOperatorDeviceInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", " "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorDevice.Invalid", result.Error!.Value.Code);
    }

    /// <summary>
    /// `26-82`, the handler's own half of the fix, stated against the port's contract rather than
    /// against Postgres: when <see cref="IOperatorDeviceRepository.SaveAsync"/> reports that a
    /// concurrent caller already committed this exact pair, the losing call must still succeed, must
    /// reapply its own token to the winner's row, and must not leave a second row behind.
    /// <c>RegisterOperatorDeviceConcurrencyTests</c> proves the same behaviour against a real Postgres
    /// and a real race; this one proves the handler alone does the right thing with the signal, with no
    /// container in the loop.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenItsInsertLosesARace_RefreshesTheWinnersRowAndSucceeds()
    {
        var devices = new FakeOperatorDeviceRepository();
        var clock = new FakeClock(Now);
        // The winner: already committed, holding the older token, by the time this handler's own insert
        // is attempted. Seeded (not saved through the handler) so the handler's own FindAsync still
        // answers null on its first read - which is exactly the check-then-act window this item exists
        // to close.
        var winner = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore,
            "android", "token-from-the-winner", Now);
        var racing = new ConflictOnFirstInsertRepository(devices, winner);
        var handler = new RegisterOperatorDeviceHandler(racing, new FakeIdGenerator(), clock);

        var result = await handler.HandleAsync(
            new Application.UseCases.RegisterOperatorDevice.RegisterOperatorDevice(
                OperatorId, SiteId, "installation-1", PushProvider.RuStore, "android", "token-from-the-loser"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, racing.SaveAttempts);
        var all = await devices.ListActiveForOperatorAsync(OperatorId, CancellationToken.None);
        var only = Assert.Single(all);
        Assert.Equal(winner.Id, only.Id);
        // The losing call's own write was reapplied, not discarded - the whole point of the retry.
        Assert.Equal("token-from-the-loser", only.Token);
    }

    /// <summary>Delegates every read to a real <see cref="FakeOperatorDeviceRepository"/> untouched. On
    /// the first <c>SaveAsync</c> it commits the winner's row and then raises exactly what
    /// <c>OperatorDeviceRepository</c>'s own unique-violation clause raises - so the handler under test
    /// is answering the real port contract, not a mock's invented one.</summary>
    private sealed class ConflictOnFirstInsertRepository(FakeOperatorDeviceRepository inner, OperatorDevice winner)
        : IOperatorDeviceRepository
    {
        private int _saveAttempts;

        public int SaveAttempts => _saveAttempts;

        public Task<OperatorDevice?> FindAsync(OperatorId operatorId, string installationId, CancellationToken cancellationToken) =>
            inner.FindAsync(operatorId, installationId, cancellationToken);

        public Task<OperatorDevice?> FindByDeviceAsync(OperatorId operatorId, string deviceId, CancellationToken cancellationToken) =>
            inner.FindByDeviceAsync(operatorId, deviceId, cancellationToken);

        public Task<OperatorDevice?> FindActiveByTokenAsync(PushProvider provider, string token, CancellationToken cancellationToken) =>
            inner.FindActiveByTokenAsync(provider, token, cancellationToken);

        public Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.ListActiveForOperatorAsync(operatorId, cancellationToken);

        public async Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _saveAttempts) == 1)
            {
                inner.Seed(winner);
                throw new OperatorDeviceConcurrencyConflictException(device.OperatorId, device.InstallationId);
            }

            await inner.SaveAsync(device, cancellationToken);
        }
    }
}
