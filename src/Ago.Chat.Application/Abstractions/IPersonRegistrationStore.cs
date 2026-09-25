using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `adr/0184` decision 2: the write behind chat's <c>PersonRegistered</c> consumer - a module (today, AGO
/// Calendar) took a booking with no chat origin, minted a person id locally, and this is how the person
/// comes to exist in the account's registry under exactly that id.
///
/// <para><b>One transaction, idempotent on the person id (CLAUDE.md rule 5).</b> The person row and the
/// contact details the announcement carried (the phone, the typed name) are written together or not at
/// all, and a person who already exists - a redelivery, or a visitor chat had already seen, which the
/// publisher should not announce but a consumer must not depend on - is left exactly as they are:
/// <em>nothing</em> about an existing person is overwritten, because the registry's own facts are
/// authoritative over a booking form's. A dedicated port rather than <see cref="IVisitorRepository"/> +
/// <see cref="IVisitorContactDetailRepository"/> in sequence, because two <c>SaveChangesAsync</c> calls
/// would be two transactions, and a crash between them would leave a person with no phone.</para>
/// </summary>
public interface IPersonRegistrationStore
{
    /// <returns><see cref="PersonRegistrationOutcome.Created"/> when this call created the person,
    /// <see cref="PersonRegistrationOutcome.AlreadyExisted"/> when a person under that id was already
    /// there (nothing written), <see cref="PersonRegistrationOutcome.SiteUnknown"/> when the account
    /// the announcement names does not exist here at all (nothing written - a fact to log, not to
    /// retry).</returns>
    Task<PersonRegistrationOutcome> RegisterIfAbsentAsync(
        Visitor person, IReadOnlyList<VisitorContactDetail> contactDetails, CancellationToken cancellationToken);
}

public enum PersonRegistrationOutcome
{
    Created,
    AlreadyExisted,
    SiteUnknown,
}
