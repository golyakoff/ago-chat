using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevokeOperatorDevice;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeOperatorDevice;

public class RevokeOperatorDeviceHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (RevokeOperatorDeviceHandler Handler, FakeOperatorDeviceRepository Devices) CreateFixture()
    {
        var devices = new FakeOperatorDeviceRepository();
        var handler = new RevokeOperatorDeviceHandler(devices, new FakeClock(Now));
        return (handler, devices);
    }

    [Fact]
    public async Task HandleAsync_RevokesTheDevice()
    {
        var (handler, devices) = CreateFixture();
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now);
        devices.Seed(device);

        var result = await handler.HandleAsync(
            new Application.UseCases.RevokeOperatorDevice.RevokeOperatorDevice(OperatorId, "installation-1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = await devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        Assert.NotNull(reloaded!.RevokedAt);
    }

    /// <summary>The design's own contract: DELETE is idempotent, including for a row that never
    /// existed - the caller cannot distinguish "already gone" from "never registered".</summary>
    [Fact]
    public async Task HandleAsync_WhenNoSuchDeviceExists_StillSucceeds()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(
            new Application.UseCases.RevokeOperatorDevice.RevokeOperatorDevice(OperatorId, "never-registered"), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_CalledTwice_StillSucceeds_AndKeepsTheFirstRevocationInstant()
    {
        var (handler, devices) = CreateFixture();
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now);
        devices.Seed(device);

        await handler.HandleAsync(
            new Application.UseCases.RevokeOperatorDevice.RevokeOperatorDevice(OperatorId, "installation-1"), CancellationToken.None);
        var firstRevocation = (await devices.FindAsync(OperatorId, "installation-1", CancellationToken.None))!.RevokedAt;

        var result = await handler.HandleAsync(
            new Application.UseCases.RevokeOperatorDevice.RevokeOperatorDevice(OperatorId, "installation-1"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var reloaded = await devices.FindAsync(OperatorId, "installation-1", CancellationToken.None);
        Assert.Equal(firstRevocation, reloaded!.RevokedAt);
    }

    [Fact]
    public async Task HandleAsync_NeverRevokesAnotherOperatorsDeviceWithTheSameInstallationId()
    {
        var (handler, devices) = CreateFixture();
        var otherOperatorId = new OperatorId(Guid.NewGuid());
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, otherOperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now);
        devices.Seed(device);

        await handler.HandleAsync(
            new Application.UseCases.RevokeOperatorDevice.RevokeOperatorDevice(OperatorId, "installation-1"), CancellationToken.None);

        var untouched = await devices.FindAsync(otherOperatorId, "installation-1", CancellationToken.None);
        Assert.Null(untouched!.RevokedAt);
    }
}
