using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-03`/`adr/0179`: the write-side port for <see cref="OperatorDevice"/> - its own port, the same
/// "its own aggregate, its own transaction boundary" reasoning <see cref="IWebhookEndpointRepository"/>'s
/// own remarks give for not folding into a hypothetical <see cref="IOperatorRepository"/> method.
///
/// <para><b>Why this lives in Application, not Infrastructure (CLAUDE.md rule 1/2).</b> Every use case
/// that touches a device row - the two `Api` routes this item ships, and `OperatorRemovedConsumer`'s
/// added call - must be testable against a fake that never opens a connection. The alternative,
/// injecting <c>AgoChatDbContext</c> (or an EF `DbSet`) directly into a handler, would make Application
/// depend on Npgsql/EF Core, which the dependency rule forbids, and would mean nothing above
/// Infrastructure could be unit-tested without a real Postgres.</para>
///
/// <para><b>Why EF and not a Dapper read store (`adr/0004`).</b> Every real caller of this port both
/// reads a device row and writes a revocation or a refresh in the same flow, against a row with a real
/// invariant (`OperatorDevice`'s own "a revoked device sends nothing until a fresh registration
/// revives it") - `adr/0004` puts writes behind EF. A separate Dapper store for
/// <see cref="ListActiveForOperatorAsync"/> alone, a handful of rows keyed by one indexed column, would
/// be a second port for the same table with no query it could express better than EF already does -
/// the identical judgement `ListMyTenanciesHandler`'s own remarks record for its own small read.</para>
/// </summary>
public interface IOperatorDeviceRepository
{
    Task<OperatorDevice?> FindAsync(OperatorId operatorId, string installationId, CancellationToken cancellationToken);

    /// <summary>The restored-backup case (`adr/0179` §1): a token must never be live on two rows, so
    /// the upsert handler looks up whoever else currently holds this exact `(provider, token)` before
    /// writing its own row. `null` once revoked - the partial unique index
    /// (`OperatorDeviceConfiguration`'s own remarks) is what actually enforces "never two live rows",
    /// this is only the read side of keeping that true.</summary>
    Task<OperatorDevice?> FindActiveByTokenAsync(PushProvider provider, string token, CancellationToken cancellationToken);

    /// <summary>The shape `26-05`'s fan-out will query - named and tested now, unused until that item
    /// exists (this item's own Done-when). A revoked device must be provably invisible here, since a
    /// push sent to one would be exactly the "sends nothing until a fresh registration revives it"
    /// rule this port exists to keep true.</summary>
    Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken);

    Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken);
}
