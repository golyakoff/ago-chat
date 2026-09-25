using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `adr/0184` decision 2: creates a Person the account's registry has never seen, under the id a module
/// minted, together with the contact details the announcement carried - one transaction, idempotent.
///
/// <para><b>Idempotency is the primary key, not a pre-read.</b> The <c>AnyAsync</c> below is the cheap
/// fast path for the ordinary redelivery; the real arbiter is <c>PK_visitors</c>, which two concurrent
/// deliveries of the same announcement both race for and exactly one wins - the loser's own insert fails
/// on the key and is reported as <see cref="PersonRegistrationOutcome.AlreadyExisted"/>, never rethrown
/// into the consumer's retry policy (the identical translation <c>VisitorRepository.SaveAsync</c> already
/// makes for the same constraint, for the same reason: a lost race on a person's own row is an ordinary
/// outcome, not a fault).</para>
///
/// <para><b>The account is checked, not assumed.</b> <c>visitors.site_id</c> is a real foreign key; an
/// announcement naming an account this deployment does not hold would fail on it, so the check comes
/// first and answers <see cref="PersonRegistrationOutcome.SiteUnknown"/> - a fact for the consumer to log
/// and ack, since no retry will make the account appear.</para>
/// </summary>
public sealed class PersonRegistrationStore(AgoChatDbContext db) : IPersonRegistrationStore
{
    public async Task<PersonRegistrationOutcome> RegisterIfAbsentAsync(
        Visitor person, IReadOnlyList<VisitorContactDetail> contactDetails, CancellationToken cancellationToken)
    {
        if (!await db.Sites.AnyAsync(s => s.Id == person.SiteId, cancellationToken))
        {
            return PersonRegistrationOutcome.SiteUnknown;
        }

        if (await db.Visitors.AnyAsync(v => v.Id == person.Id, cancellationToken))
        {
            return PersonRegistrationOutcome.AlreadyExisted;
        }

        db.Visitors.Add(person);
        db.VisitorContactDetails.AddRange(contactDetails);

        try
        {
            // One SaveChangesAsync is one transaction: the person and their details land together or
            // not at all (IPersonRegistrationStore's own remarks on why this is not two repository calls).
            await db.SaveChangesAsync(cancellationToken);
            return PersonRegistrationOutcome.Created;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_visitors",
        })
        {
            // A concurrent delivery of the same announcement got there first - see the class remarks.
            db.ChangeTracker.Clear();
            return PersonRegistrationOutcome.AlreadyExisted;
        }
    }
}
