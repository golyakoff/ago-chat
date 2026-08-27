using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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
public sealed class SiteRegistrationRepository(AgoChatDbContext db) : ISiteRegistrationRepository
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
