using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-59`/`adr/0147`: the write side of "granting the module carries over contacts collected before
/// the grant" - what <see cref="UseCases.EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwnerHandler"/>
/// calls once a grant has succeeded, to record the second, independent fact that a retroactive
/// carry-over is now owed for this site. See <c>Ago.Chat.Infrastructure.Postgres.Persistence.ContactCarryoverRequestEntity</c>'s
/// own remarks for why this is a request row a background job later drains in bounded batches, not
/// work this call does itself - the item's own trap: "nothing user-facing may wait on it."
///
/// <para><b>Declared here, implemented in Infrastructure</b> - the same dependency-rule reasoning
/// every port in this folder states: the grant handler must not know this is a Postgres upsert, or it
/// could not be tested without a database.</para>
/// </summary>
public interface IContactCarryoverRequestStore
{
    /// <summary>
    /// Idempotent and unconditional, the identical "deliberately unconditional... a re-grant re-runs
    /// the identical, idempotent seeding" posture <c>RoleRepository.AddPermissionsAsync</c>'s own
    /// remarks describe for permission seeding: calling this twice for the same site resets the
    /// carry-over to start again from the top, which is both harmless (every batch it stages is itself
    /// idempotent at the far side) and is how a re-grant repairs a site whose earlier carry-over never
    /// finished - the same repair-by-ordinary-grant path `23-102` established for permissions.
    /// </summary>
    Task RequestAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken);
}
