using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-72`: writes through the caller's own scoped <see cref="AgoChatDbContext"/> via
/// <c>ExecuteSqlInterpolatedAsync</c>, deliberately not <see cref="AccessRecordRepository"/>'s fresh
/// <see cref="Npgsql.NpgsqlDataSource"/> connection - <see cref="IRoleChangeRecordRepository"/>'s own
/// remarks state why: this record must commit or roll back atomically with the role swap and the
/// last-administrator check that authorised it, so it has to run on the same connection and
/// ambient transaction <c>ChangeOperatorRoleHandler</c>'s other writes already share (`EfUnitOfWork`'s
/// own remarks on how that participation happens with no extra wiring). EF's bulk `Execute*Async` family
/// joins <c>Database.CurrentTransaction</c> the same way <c>SaveChangesAsync</c> does, so no explicit
/// transaction handling is needed here at all.
/// </summary>
public sealed class RoleChangeRecordRepository(AgoChatDbContext db) : IRoleChangeRecordRepository
{
    public Task RecordAsync(RoleChangeRecordToWrite record, CancellationToken cancellationToken) =>
        // `::text[]` is not decoration - an *empty* string[] parameter gives Npgsql nothing to infer an
        // element type from ("could not determine data type of parameter"), the identical reason
        // AccessRecordRepository's own remarks give for its explicit NpgsqlDbType on every nullable
        // column there. Every real caller today assigns at least one role before this handler can ever
        // run, but the cast costs nothing and removes the failure mode entirely rather than relying on
        // that always remaining true.
        //
        // `25-41`: changed_by_operator_id's own parameter is now `Guid?` (record.ChangedByOperatorId?.Value)
        // - see IRoleChangeRecordRepository's own remarks for why an automatic demotion has no operator
        // to name honestly here, and Stage25WidenRoleChangeRecordActor's own migration for the column
        // this widens.
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            insert into role_change_records
                (id, site_id, changed_by_operator_id, changed_operator_id, previous_role_names, new_role_name, changed_at)
            values
                ({record.Id}, {record.SiteId.Value}, {record.ChangedByOperatorId?.Value}, {record.ChangedOperatorId.Value},
                 {record.PreviousRoleNames.ToArray()}::text[], {record.NewRoleName}, {record.ChangedAt})
            """,
            cancellationToken);
}
