using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeOperatorDevice;

/// <summary>
/// `26-03`/`adr/0179` §1: sign-out revocation - called before the client discards its own access token,
/// which is why this route carries no further permission check beyond `RequireOperatorIdentity`
/// (revoking your own device needs nobody else's say-so). Idempotent the same way
/// <see cref="OperatorDevice.Revoke"/> itself is: a missing row and an already-revoked row both read as
/// success, matching the design's own "-> 204, also 204 when there is no such row" contract - unlike
/// <see cref="RevokeWebhookEndpointHandler"/>, which pre-checks state before calling a domain method
/// that throws on a repeat, `OperatorDevice.Revoke` is idempotent by its own construction (`adr/0179`),
/// so there is nothing here to pre-check.
/// </summary>
public sealed class RevokeOperatorDeviceHandler(IOperatorDeviceRepository devices, IClock clock)
{
    public async Task<Result> HandleAsync(RevokeOperatorDevice command, CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(command.OperatorId, command.InstallationId, cancellationToken);
        if (device is null)
        {
            return Result.Success();
        }

        device.Revoke(clock.UtcNow);
        await devices.SaveAsync(device, cancellationToken);
        return Result.Success();
    }
}
