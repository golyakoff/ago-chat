using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `10-02`: one fixed permission set, seeded as a `roles` row, for the bootstrap transaction below -
/// not a Domain or Application entity, matching `Ago.Chat.Infrastructure.Postgres.RoleRecord`'s own
/// remarks ("nothing above `PermissionChecker` manages roles yet, so there is nothing for a richer
/// model to buy"). This type is shaped around the one thing that needs to *write* a role today
/// (`RegisterSiteHandler`), not a general role-management surface `authorization.md` already defers.
/// </summary>
public sealed record RoleSeed(Guid Id, string Name, IReadOnlyList<string> Permissions);

/// <summary>The whole bootstrap package `RegisterSiteHandler` builds and
/// <see cref="ISiteRegistrationRepository.TryRegisterAsync"/> persists as one transaction: `Site` +
/// both fixed roles + `Operator` + both `operator_roles` rows, plus (`24-03`) zero or more
/// <see cref="AcceptanceRecord"/> rows. See `RegisterSiteHandler`'s own remarks for why this is one
/// wider transaction than `data-model.md`'s usual "one aggregate per transaction".
///
/// <para><b>`24-03`: <paramref name="Acceptances"/> is staged into the same transaction, not written
/// through <c>RecordAcceptanceHandler</c> afterwards.</b> `RecordAcceptanceHandler`
/// (<c>Ago.Chat.Application.UseCases.RecordAcceptance</c>) calls its own
/// <see cref="IAcceptanceRepository.SaveAsync"/>, which issues its own, independent
/// <c>SaveChangesAsync</c> - calling it as a second step after <see cref="ISiteRegistrationRepository.TryRegisterAsync"/>
/// committed would mean a crash between the two calls leaves a site with no evidence it ever showed the
/// tenant an agreement, exactly the defect this item exists to prevent (`RegisterSiteHandler`'s own
/// remarks on why the five-row bootstrap package is one transaction rather than five: "a partial
/// failure here must not leave a site with no roles" - the identical reasoning applies to "must not
/// leave a site with no evidence of accepted terms"). So `RegisterSiteHandler` builds each
/// <see cref="AcceptanceRecord"/> directly through its own `AcceptanceRecord.ForTenant` factory (the
/// same domain type <c>RecordAcceptanceHandler</c> uses, just not through that handler class) and
/// hands the finished rows here, where <c>SiteRegistrationRepository.TryRegisterAsync</c> stages them
/// onto the same <c>DbContext</c> as everything else in this record, committed by the one
/// <c>SaveChangesAsync</c> call that follows.</para></summary>
public sealed record SiteRegistration(
    Site Site, Operator Operator, RoleSeed OperatorRole, RoleSeed AdminRole, IReadOnlyList<AcceptanceRecord> Acceptances);

/// <summary>
/// `10-02`: the write side of the one genuinely multi-row provisioning step in this codebase -
/// `Site` + two `Role`s + `Operator` + two `operator_roles` rows, committed together or not at all.
/// Its own port (not folded into <see cref="ISiteRepository"/> or <see cref="IOperatorRepository"/>)
/// because neither of those single-aggregate ports has any business writing rows that belong to a
/// different aggregate - this is deliberately the one place that does, and naming it separately keeps
/// that scope visible rather than quietly widening an existing port's contract.
/// </summary>
public interface ISiteRegistrationRepository
{
    /// <summary>Persists the whole <paramref name="registration"/> package in one transaction, or
    /// none of it. Returns <c>false</c> without partially writing anything if
    /// <c>(</c><see cref="Operator.ExternalSubjectId"/><c>, </c><see cref="Operator.SiteId"/><c>)</c>
    /// already resolves to an existing `operators` row - the database's own composite unique index
    /// (`13-07`/`adr/0068`: "unique when present" on the pair, widened from the single-column index
    /// `adr/0022` originally described) is what actually decides this under a race, the same "let a
    /// real constraint be the source of truth for a compare-and-set decision" shape
    /// <see cref="IWebhookDeliveryRepository.SaveAsync"/> already established for its own duplicate
    /// insert. <c>RegisterSiteHandler</c>'s own remarks (at this method's call site) explain why, once
    /// `siteId` is freshly generated on every call, this path is effectively unreachable in ordinary
    /// operation rather than the reachable race it guarded before `13-07`.</summary>
    Task<bool> TryRegisterAsync(SiteRegistration registration, CancellationToken cancellationToken);
}
