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
///
/// <para><b>`26-82`, found live on the demo cluster: the upsert below was check-then-act, and
/// `26-06`'s own Android client races it against itself.</b> Three call sites - sign-in,
/// `onNewToken`, and a periodic `WorkManager` job - can reach `PUT /api/v1/me/devices/{installationId}`
/// for the identical `(operatorId, installationId)` pair within milliseconds of an app launch or a
/// token rotation. When two of them both see <see cref="IOperatorDeviceRepository.FindAsync"/> answer
/// null before either has committed, both INSERT, and the loser hit
/// `ux_operator_devices_operator_installation` - a raw `23505` that this handler's `ArgumentException`
/// catch did not cover, so it left as an unhandled 500 and the client's own catch-all silently filed it
/// as "not registered this time" (9 occurrences in one 30-minute window from one real phone).
///
/// The read at the upsert below cannot see a row that has not been saved yet, no matter how it is
/// written - so the fix is not a better read, it is `OperatorDeviceConfiguration`'s own unique index,
/// which turns the loser's `SaveAsync` into <see cref="OperatorDeviceConcurrencyConflictException"/>
/// (`OperatorDeviceRepository`'s own remarks). Caught here, once: the loser's own
/// <see cref="OperatorDeviceId"/> lost the race and is worthless, but the winner's row is committed and
/// visible to a fresh read, and the write the loser was carrying is exactly a
/// <see cref="OperatorDevice.Refresh"/> - so it re-reads and applies it, landing in precisely the state
/// two *sequential* calls would have produced. The identical shape `StartConversationHandler` uses for
/// the same class of race on `ix_conversations_one_open_per_visitor`/`PK_visitors` (`25-67`), differing
/// only in that this one reapplies its write instead of discarding it.
///
/// <b>One retry, not a bounded loop</b>, unlike `MessageBatchWriter`/`TransferConversationHandler`'s own
/// jittered `MaxAttempts`: those retry an UPDATE that can lose its `xmin` compare-and-set again on the
/// next attempt, so the bound is doing real work. Here the retry is an UPDATE of a row this context now
/// tracks by its own primary key, with no `xmin` check configured on this entity at all - it cannot
/// collide with `ux_operator_devices_operator_installation` a second time, so a second attempt could
/// only ever be dead code.</para>
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
                try
                {
                    await devices.SaveAsync(device, cancellationToken);
                }
                catch (OperatorDeviceConcurrencyConflictException)
                {
                    // `26-82`: lost the race - see this class's own remarks. The winner committed
                    // first, so it is there to be read now, and this call's own write is exactly the
                    // Refresh below.
                    await RefreshWinnerAsync(command, now, cancellationToken);
                }
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

    /// <summary>
    /// `26-82`: the losing INSERT's own write, reapplied to the row that won. Deliberately re-reads
    /// through the port rather than reusing the local <see cref="OperatorDevice"/> that just lost - that
    /// instance carries an <see cref="OperatorDeviceId"/> no row will ever have, and (in the EF adapter)
    /// was detached by the translation itself, so it is not a thing that could be saved even if this
    /// wanted to.
    /// </summary>
    private async Task RefreshWinnerAsync(
        RegisterOperatorDevice command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var winner = await devices.FindAsync(command.OperatorId, command.InstallationId, cancellationToken);
        if (winner is null)
        {
            // Unreachable by construction: the unique index that produced the conflict only fires when
            // a matching row already exists, and no write path removes one in the microseconds that
            // follow - sign-out and operator removal both Revoke, leaving the row in place
            // (OperatorDevice's own remarks), and the one thing that does delete it, site erasure's own
            // FK cascade (OperatorDeviceConfiguration's own remarks), has taken the whole tenancy with
            // it and there is no registration left to complete. Thrown rather than silently treated as
            // "no device" - the same "do not paper over a broken invariant" choice
            // StartConversationHandler's own two race catches make.
            throw new InvalidOperationException(
                $"Device registration for operator {command.OperatorId.Value} and installation "
                + $"'{command.InstallationId}' conflicted with a row that then could not be read back.");
        }

        winner.Refresh(command.Provider, command.Platform, command.Token, now);
        await devices.SaveAsync(winner, cancellationToken);
    }
}
