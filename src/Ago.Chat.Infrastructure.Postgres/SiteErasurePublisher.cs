using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-82`: <see cref="ISiteErasurePublisher"/>'s own implementation - see that interface's own remarks
/// for why this exists apart from <see cref="DemoTenantRepository"/>.
///
/// <para><b>The delete itself is still raw SQL, not a change-tracked <c>DbSet</c> operation.</b>
/// <c>DemoTenantRepository.DeleteSiteAsync</c>'s former remarks explained why a cross-aggregate delete
/// must never go through EF's change tracker - that reasoning is unchanged here, and
/// <see cref="RelationalDatabaseFacadeExtensions.ExecuteSqlInterpolatedAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.FormattableString,System.Threading.CancellationToken)"/>
/// gives the identical untracked-statement semantics Dapper did. What differs is only that this
/// statement now runs on <see cref="AgoChatDbContext"/>'s own connection, inside an explicit transaction
/// it shares with the outbox insert below - Dapper's own <c>NpgsqlDataSource</c> connection had nothing
/// else to be atomic with, which is exactly the gap this item closes.</para>
///
/// <para><b>Same constructor shape as <c>SeatChangeApplier</c></b> (<c>AgoChatDbContext</c>,
/// <see cref="IOutboxWriter"/>, <see cref="IIdGenerator"/>) - DI resolves <see cref="IOutboxWriter"/> to
/// an instance already bound to the identical scoped <see cref="AgoChatDbContext"/>, so
/// <see cref="IOutboxWriter.Enqueue"/> and <see cref="AgoChatDbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>
/// below act on the same connection the delete just ran on, and the same transaction commits both.
/// </para>
///
/// <para><b>What the one statement below reaches, and what it does not</b> - carried over from
/// <c>DemoTenantRepository.DeleteSiteAsync</c>'s own former remarks, stated here because a deletion
/// that quietly misses something is worse than one that says what it misses (`adr/0058` has the full
/// account). Every table that holds a demo tenant's data carries a foreign key to `sites` with
/// `ON DELETE CASCADE`: `visitors`, `conversations` - and `messages` through it - `attachments`,
/// `channel_identities`, `operators`, `operator_roles` through them, `roles`, `webhook_endpoints` and
/// `webhook_deliveries` through them. That is why this is one statement and not a hand-ordered sequence
/// of deletes: a list written here would be a second, weaker copy of the schema, and it would silently
/// stop being complete the first time somebody adds a table - which is exactly how erasure becomes
/// partial; `DemoTenantLifecycleTests` asserts emptiness table by table against
/// `personal-data.md`'s own list rather than trusting this comment. What this statement does <b>not</b>
/// reach: the object store and the identity provider, both handled by the caller
/// (<c>DemoTenantExpiryJob.RemoveAsync</c>) because both can fail independently and neither can join
/// this transaction; backups, until `15-02`'s retention window ages them out; and any node queue, trace
/// or log line. `outbox` itself is reached now - that is this item's whole point - but only by the one
/// row this method stages, never by anything already sitting there naming this site from an earlier
/// publish.</para>
/// </summary>
public sealed class SiteErasurePublisher(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator)
    : ISiteErasurePublisher
{
    public async Task<bool> EraseAndPublishAsync(SiteId siteId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var rowsDeleted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"delete from sites where id = {siteId.Value}", cancellationToken);

        if (rowsDeleted == 0)
        {
            // Already gone - a previous cycle's crash between this commit and a later step, or two
            // replicas racing the same tenant. Nothing changed, so nothing to publish; disposing the
            // transaction without a commit is the rollback, and there is nothing in it to roll back.
            return false;
        }

        outbox.Enqueue(SiteErasedMapper.ToEnvelope(siteId.Value, occurredAt, idGenerator));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
