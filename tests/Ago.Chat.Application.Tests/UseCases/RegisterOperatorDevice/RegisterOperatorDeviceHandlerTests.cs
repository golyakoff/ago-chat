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
}
