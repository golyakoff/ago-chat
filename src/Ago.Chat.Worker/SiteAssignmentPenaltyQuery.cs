using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-05`: the one read `SkipLockedAssignmentClaimer` and `RedisLockAssignmentClaimer` both make for
/// their own second pass - `sites.assignment_penalty_seconds`, queried directly on the caller's own
/// <see cref="AgoChatDbContext"/> (and therefore the caller's own connection and transaction) rather
/// than behind a port. Deliberately not `ISiteRepository.GetByIdAsync` (loads the whole `Site`
/// aggregate - every widget-config field, every offline-auto-reply rule - for one `int`) and
/// deliberately not the cached `SiteConfigDto` the widget handshake reads (`caching.md`'s own remarks:
/// that DTO has no `AssignmentPenaltySeconds` field at all, on purpose). `CLAUDE.md` rule 8's textbook
/// case: this column is configuration a write decision depends on, so it is read fresh, inside the
/// exact transaction that goes on to perform the compare-free claim if the conversation turns out to
/// be old enough - never from a cache that could still be holding a value up to five minutes stale.
///
/// <para>A shared static method rather than two copies, the identical "one query, two callers, no
/// silent drift" reasoning <see cref="ConversationAssignmentIntervalSql"/>'s own remarks give for
/// itself - and issued the same way, through <c>db.Database.SqlQuery&lt;int&gt;</c> rather than a LINQ
/// projection over <c>db.Sites</c>, because a scalar read of one column by primary key has no
/// aggregate-loading behaviour worth going through EF's change tracker for.</para>
///
/// <para>Never null: every `sites` row has this column (`NOT NULL DEFAULT 120`,
/// `Stage23AddSiteAssignmentPenalty`), and both claimers only ever call this for a `siteId` a waiting
/// conversation just named, which cannot itself be missing its own site row (the same referential
/// assumption `WaitingConversationClaimQuery`'s own caller already relies on).</para>
/// </summary>
internal static class SiteAssignmentPenaltyQuery
{
    public static Task<int> GetSecondsAsync(AgoChatDbContext db, SiteId siteId, CancellationToken cancellationToken) =>
        db.Database
            // `Database.SqlQuery<T>` for a primitive `T` requires the single returned column to be
            // named `Value` - EF's own convention, not a choice made here.
            .SqlQuery<int>($"SELECT assignment_penalty_seconds AS \"Value\" FROM sites WHERE id = {siteId.Value}")
            .SingleAsync(cancellationToken);
}
