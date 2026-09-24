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
    /// <para><b>Scoped to `ux_operator_devices_operator_installation` by name, nothing wider</b> - the
    /// same "translate exactly the constraint this call site can explain" precedent
    /// <see cref="ConversationRepository"/>'s own three catches set. The sibling index on this very
    /// table, `ux_operator_devices_provider_token_active`, means something else entirely (a token live
    /// on somebody else's row - `adr/0179` §1's restored-backup case, which
    /// <c>RegisterOperatorDeviceHandler</c>'s own step 1 revokes rather than retries), so folding both
    /// into one translated type would hand the handler a conflict its re-read-and-refresh cannot
    /// actually resolve. A losing INSERT in the real case violates <em>both</em> at once - two racing
    /// calls from one device carry the identical token - and which one Postgres names is not a coin
    /// toss: it fills a table's unique indexes in the order it lists them, which is by index oid, and
    /// `Stage26AddOperatorDevices` creates this one first. Not taken on that reading of Postgres alone:
    /// it is the constraint the live demo cluster's own nine 23505s named, and
    /// <c>RegisterOperatorDeviceConcurrencyTests</c>'s same-token race asserts that exactly one
    /// conflict of *this* translated type is observed, so a future Postgres that chose differently
    /// would fail that test rather than quietly resurrect the 500.</para>
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
            ConstraintName: "ux_operator_devices_operator_installation",
        })
        {
            db.ChangeTracker.Clear();
            throw new OperatorDeviceConcurrencyConflictException(device.OperatorId, device.InstallationId);
        }
    }
}
