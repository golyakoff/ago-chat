using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-82`: erases exactly one site row and, only if a row actually disappeared, stages
/// <see cref="Ago.Chat.Contracts.SiteErased"/> in the identical transaction (rule 4) - the one thing
/// <c>IDemoTenantRepository.DeleteSiteAsync</c> never did before this item, which is why
/// `role_assignment_projections`, in a different database belonging to a different product, was found
/// still holding rows for tenants this database had already forgotten
/// (`docs/backlog/25-82-*.md`'s own count).
///
/// <para><b>A second port, not a new method on <see cref="IDemoTenantRepository"/>.</b>
/// <see cref="IDemoTenantRepository"/>'s own implementation is deliberately Dapper over
/// <c>NpgsqlDataSource</c> - a connection-pool handle, safe to capture for the whole lifetime of the
/// singleton <c>DemoTenantExpiryJob</c> because it carries no per-operation state. Committing the site
/// delete and the outbox insert atomically needs <c>AgoChatDbContext</c>, which must never be held that
/// long - EF's own change tracker is not meant to outlive one unit of work. Folding this onto
/// <see cref="IDemoTenantRepository"/> would have forced its already-safe read methods
/// (<see cref="IDemoTenantRepository.ListExpiredAsync"/>,
/// <see cref="IDemoTenantRepository.ListAttachmentObjectKeysAsync"/>) to pay for a lifetime constraint
/// only this one write actually needs. This port instead exists to be resolved fresh, once per tenant
/// removed, from <c>IServiceScopeFactory.CreateAsyncScope()</c> - the identical shape
/// <c>Ago.Chat.Worker.SiteErasureJob</c> and <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> already
/// use in this same project for the identical reason.</para>
///
/// <para><b>Idempotent by construction</b> (rule 5): a site already gone means the raw <c>DELETE</c>
/// affects zero rows, and the implementation stages nothing when that happens - a retried or racing
/// call lands on the identical, already-true fact rather than a duplicate event.</para>
/// </summary>
public interface ISiteErasurePublisher
{
    /// <returns><see langword="true"/> if a site row was actually removed by this call (and the event
    /// staged with it); <see langword="false"/> if the site was already gone.</returns>
    Task<bool> EraseAndPublishAsync(SiteId siteId, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}
