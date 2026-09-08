using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`23-59`'s <see cref="IContactCarryoverRequestStore"/> adapter - a single upsert, the same
/// "one statement, Postgres arbitrates the conflict" shape <c>BookingStore</c> (`ago-calendar`) uses
/// for its own lead-card upsert, for the identical reason: a read-then-insert-with-retry would have a
/// window a concurrent re-grant could walk through, and Postgres's own <c>ON CONFLICT</c> closes it in
/// one round trip instead.</summary>
public sealed class ContactCarryoverRequestStore(AgoChatDbContext db) : IContactCarryoverRequestStore
{
    public Task RequestAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into contact_carryover_requests (site_id, requested_at, cursor_contact_id, completed_at)
            values ({siteId.Value}, {now}, null, null)
            on conflict (site_id) do update
                set requested_at = excluded.requested_at, cursor_contact_id = null, completed_at = null
            """,
            cancellationToken);
}
