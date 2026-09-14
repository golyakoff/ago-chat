namespace Ago.Chat.Contracts;

/// <summary>
/// `25-82`: the fact that a site is now completely gone from this database - published once, at the
/// instant `DELETE FROM sites` actually removes a row, so any other product holding a copy of
/// something about this tenant can drain it. `docs/architecture/personal-data.md`'s own
/// `DemoTenantExpiryJob` row already named this class of gap in general terms (`outbox` on its own
/// "does not reach" list) before this item closed it for the one case measured live:
/// `role_assignment_projections` (`22-16`) in `ago-calendar`, found holding rows for 70 of 74 distinct
/// tenants that no longer existed in `ago_chat.sites` at all.
///
/// <para><b>Only <see cref="SiteId"/> - never a foreign key a consumer could dereference.</b> By the
/// time any consumer sees this event, the row it names is already gone from this database (the delete
/// and this outbox row commit together, in one transaction - rule 4 - but publishing itself is a later,
/// separate step, `messaging.md`'s own dispatcher). The payload therefore carries the tenant's own
/// identity as a bare value, the identical discipline every other fact on this transport already
/// follows (<see cref="RoleAssignmentsChanged.SiteId"/>, <see cref="TenantSuspensionChanged.SiteId"/>).
/// </para>
///
/// <para><b>Fired only when a row was actually removed, never speculatively.</b>
/// <see cref="Ago.Chat.Application.Abstractions.ISiteErasurePublisher"/>'s own implementation checks
/// the delete's affected-row count before staging anything, so a retry against an already-gone site (a
/// crash between this commit and a later step in the same job, or two replicas racing the same tenant)
/// stages nothing a second time - the same "no fallback, ever" discipline this codebase already applies
/// to every idempotent write, made structural here rather than left to a consumer's own deduplication
/// alone.</para>
///
/// <para><b>Reusable beyond <c>DemoTenantExpiryJob</c>, deliberately not named for the one job that
/// publishes it today.</b> The identical fact would describe <c>Ago.Chat.Worker.SiteErasureJob</c>'s
/// own site deletion just as accurately, should that job ever need to publish it too - a decision this
/// item does not make (its own Scope is the one accumulation it measured), left for whoever reads that
/// job's own remarks next.</para>
/// </summary>
public sealed record SiteErased(Guid SiteId, DateTimeOffset OccurredAt, Guid CorrelationId);
