using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `10-02`: five inserts (`Site`, two `RoleRecord`s, `Operator`, two `OperatorRoleRecord`s) staged
/// against the same <see cref="AgoChatDbContext"/> and committed with one <c>SaveChangesAsync</c> -
/// EF Core wraps every pending change in one implicit transaction the same way
/// <c>IInboxChecker</c>'s own combined aggregate-plus-inbox commit already relies on (`2-05`'s
/// `UnreadCounterConsumer`), so no explicit <c>BeginTransactionAsync</c> is needed for "all five rows
/// or none" - see <see cref="ISiteRegistrationRepository"/>'s own remarks for why this is the one
/// place in this codebase that writes across more than one aggregate at once.
///
/// Catches the same unique-index violation <see cref="WebhookDeliveryRepository.SaveAsync"/> already
/// catches for its own duplicate insert - here it is `operators`' composite `(external_subject_id,
/// site_id)` unique-when-present index (`13-07`/`adr/0068`, widened from the single-column index
/// `adr/0022` originally described). See
/// <see cref="Ago.Chat.Application.UseCases.RegisterSite.RegisterSiteHandler"/>'s own remarks for why
/// this is now effectively unreachable in ordinary operation rather than a real, reachable race.
/// </summary>
public sealed class SiteRegistrationRepository(AgoChatDbContext db, IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
    : ISiteRegistrationRepository
{
    public async Task<bool> TryRegisterAsync(SiteRegistration registration, CancellationToken cancellationToken)
    {
        db.Sites.Add(registration.Site);
        db.Operators.Add(registration.Operator);
        db.Roles.Add(new RoleRecord
        {
            Id = registration.OperatorRole.Id,
            SiteId = registration.Site.Id,
            Name = registration.OperatorRole.Name,
            Permissions = [.. registration.OperatorRole.Permissions],
        });
        db.Roles.Add(new RoleRecord
        {
            Id = registration.AdminRole.Id,
            SiteId = registration.Site.Id,
            Name = registration.AdminRole.Name,
            Permissions = [.. registration.AdminRole.Permissions],
        });
        db.OperatorRoles.Add(new OperatorRoleRecord
        {
            OperatorId = registration.Operator.Id,
            RoleId = registration.OperatorRole.Id,
        });
        db.OperatorRoles.Add(new OperatorRoleRecord
        {
            OperatorId = registration.Operator.Id,
            RoleId = registration.AdminRole.Id,
        });

        // `24-03`: zero or more AcceptanceRecord rows, staged onto the same DbContext so they land
        // with the five rows above or not at all - SiteRegistration's own remarks on why this must not
        // be a second, independent SaveChangesAsync. No FK to Site (adr/0111), so there is no ordering
        // constraint between these inserts and Site's own - see AcceptanceRecordConfiguration.
        foreach (var acceptance in registration.Acceptances)
        {
            db.AcceptanceRecords.Add(acceptance);
        }

        // `22-05`/`adr/0093`: the account owner's projected fact for whichever product reads it - the
        // union of both seeded roles, because RegisterSiteHandler grants the owner both. Staged onto
        // this same DbContext, so it lands with the five rows above or not at all (rule 4) - not a
        // separate call, because there is no separate transaction to put it in. Skipped only when the
        // owner has no external identity yet, which no caller of this method produces today (every
        // registration authenticates through Keycloak first) but is guarded anyway rather than assumed.
        if (registration.Operator.ExternalSubjectId is { } ownerSubject)
        {
            var permissions = registration.OperatorRole.Permissions
                .Concat(registration.AdminRole.Permissions)
                .Distinct()
                .ToList();
            outbox.Enqueue(RoleAssignmentsChangedMapper.ToEnvelope(
                ownerSubject, registration.Site.Id.Value, permissions, clock.UtcNow, idGenerator));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // None of the five rows landed (Postgres rolled back the whole statement batch) - detach
            // all of them so a caller reusing this same DbContext instance for further work does not
            // keep tracking phantom rows EF still believes are pending, matching
            // WebhookDeliveryRepository.SaveAsync's own remarks on why this detach matters.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
