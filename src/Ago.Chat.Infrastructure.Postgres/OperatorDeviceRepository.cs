using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

public sealed class OperatorDeviceRepository(AgoChatDbContext db) : IOperatorDeviceRepository
{
    public Task<OperatorDevice?> FindAsync(OperatorId operatorId, string installationId, CancellationToken cancellationToken) =>
        db.OperatorDevices.FirstOrDefaultAsync(
            d => d.OperatorId == operatorId && d.InstallationId == installationId, cancellationToken);

    public Task<OperatorDevice?> FindByDeviceAsync(OperatorId operatorId, string deviceId, CancellationToken cancellationToken) =>
        db.OperatorDevices.FirstOrDefaultAsync(
            d => d.OperatorId == operatorId && d.DeviceId == deviceId, cancellationToken);

    public Task<OperatorDevice?> FindActiveByTokenAsync(PushProvider provider, string token, CancellationToken cancellationToken) =>
        db.OperatorDevices.FirstOrDefaultAsync(
            d => d.Provider == provider && d.Token == token && d.RevokedAt == null, cancellationToken);

    public async Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(
        OperatorId operatorId, CancellationToken cancellationToken) =>
        await db.OperatorDevices
            .Where(d => d.OperatorId == operatorId && d.RevokedAt == null)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// `26-82`: the unique-violation clause is the whole of this item's fix at the storage boundary.
    /// Translated here, not left to propagate as EF's own type - the same "the adapter is the one place
    /// in the whole call chain allowed to know which ORM raised it" shape
    /// <see cref="VisitorRepository.SaveAsync"/> and <see cref="ConversationRepository.SaveAsync"/>
    /// already establish, and the rule <c>IOperatorCapacity</c>'s own remarks state as a prohibition
    /// ("a handler must never catch <c>PostgresException</c>").
    ///
    /// <para><b>Scoped to the two identity indexes by name, nothing wider</b> - the same "translate
    /// exactly the constraint this call site can explain" precedent <see cref="ConversationRepository"/>'s
    /// own three catches set. `26-122` added `ux_operator_devices_operator_device` alongside the
    /// pre-existing `ux_operator_devices_operator_installation`: identity moved to `(operator_id,
    /// device_id)`, but the old index stays (dropping it is a contract-phase change this migration does
    /// not make, `docs/conventions/git-workflow.md`'s own expand/contract discipline), so a genuinely
    /// concurrent first-ever registration - three call sites, identical fresh installation id *and*
    /// device id - can still trip either one depending on Postgres's own index-oid fill order. Both
    /// translate to the identical <see cref="OperatorDeviceConcurrencyConflictException"/>; the sibling
    /// index on this table, `ux_operator_devices_provider_token_active`, means something else entirely (a
    /// token live on somebody else's row - `adr/0179` §1's restored-backup case, which
    /// <c>RegisterOperatorDeviceHandler</c>'s own step 1 revokes rather than retries), so it is
    /// deliberately not part of this `when` clause.</para>
    ///
    /// <para><b><see cref="ChangeTracker.Clear"/>, not a targeted detach of <paramref name="device"/></b>
    /// - the same reasoning <see cref="VisitorRepository"/>'s own clause gives: the caller's next act is
    /// a re-read that must see Postgres's truth, and this context still holds the loser's own row in
    /// <see cref="EntityState.Added"/>. Left tracked, the handler's retry <see cref="SaveAsync"/> would
    /// re-issue the very INSERT that just lost, and throw again.</para>
    /// </summary>
    public async Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken)
    {
        // Same detached-vs-tracked check as WebhookEndpointRepository.SaveAsync - a freshly Register()'d
        // device was never loaded through this context.
        if (db.Entry(device).State == EntityState.Detached)
        {
            db.OperatorDevices.Add(device);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgres
        && postgres.ConstraintName is "ux_operator_devices_operator_installation" or "ux_operator_devices_operator_device")
        {
            db.ChangeTracker.Clear();
            throw new OperatorDeviceConcurrencyConflictException(device.OperatorId, device.InstallationId, device.DeviceId);
        }
    }
}
