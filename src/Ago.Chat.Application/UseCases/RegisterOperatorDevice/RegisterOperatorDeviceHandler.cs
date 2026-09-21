using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterOperatorDevice;

/// <summary>
/// `26-03`/`adr/0179` §1: registration and rotation, both through one idempotent upsert on
/// `(operatorId, installationId)`. No permission check beyond `RequireOperatorIdentity`
/// (`Ago.Chat.Api`'s own gate) - this route only ever writes the caller's own device, never another
/// operator's, so there is no second operator's authorization to check the way
/// `RegisterWebhookEndpointHandler` checks `Permission.WebhookManage` for a site-wide resource.
/// </summary>
public sealed class RegisterOperatorDeviceHandler(IOperatorDeviceRepository devices, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result> HandleAsync(RegisterOperatorDevice command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // `adr/0179` §1, step 1: the restored-backup case - revoke any other still-live row holding
        // this exact (provider, token), before this operator's own row is written, so a token can
        // never end up live on two rows at once. OperatorDeviceConfiguration's own partial unique index
        // is the storage-level backstop for the identical rule; this is the write-path half of it.
        var holder = await devices.FindActiveByTokenAsync(command.Provider, command.Token, cancellationToken);
        if (holder is not null
            && (holder.OperatorId != command.OperatorId || holder.InstallationId != command.InstallationId))
        {
            holder.Revoke(now);
            await devices.SaveAsync(holder, cancellationToken);
        }

        // `adr/0179` §1, step 2: the upsert itself. OperatorDevice.Register/Refresh do the actual
        // validation (OperatorDevice's own remarks on why that rule lives in Domain, not here) - this
        // handler only decides which of the two to call, and turns a validation failure into the same
        // Result shape every other use case returns, the identical SendVisitorMessageHandler idiom for
        // MessageBody's own bounded-value construction.
        var existing = await devices.FindAsync(command.OperatorId, command.InstallationId, cancellationToken);
        try
        {
            if (existing is null)
            {
                var id = new OperatorDeviceId(idGenerator.NewId(now));
                var device = OperatorDevice.Register(
                    id, command.SiteId, command.OperatorId, command.InstallationId, command.Provider,
                    command.Platform, command.Token, now);
                await devices.SaveAsync(device, cancellationToken);
            }
            else
            {
                // Revives a previously revoked row too (OperatorDevice.Refresh's own remarks) - a
                // sign-out followed by a later sign-in on the same install is a fresh registration of
                // the same device, not a new one.
                existing.Refresh(command.Provider, command.Platform, command.Token, now);
                await devices.SaveAsync(existing, cancellationToken);
            }
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.OperatorDeviceInvalid(ex.Message);
        }

        return Result.Success();
    }
}
