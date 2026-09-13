namespace Ago.Chat.Contracts;

/// <summary>
/// `22-08`/`adr/0149` rule 1/`adr/0166`: the lease itself, riding the identical propagation shape
/// <see cref="ModuleQuantityGranted"/> and <see cref="RoleAssignmentsChanged"/> already establish - a
/// snapshot of the *current* fact, never a delta, keyed for per-tenant ordering only (rule 6).
///
/// <para><b>Consumed by every module that gates a write on a tenant's own standing - `ago-calendar`'s
/// own <c>TenantSuspensionChangedConsumer</c> is the first and, today, the only one.</b> Chat never
/// learns what a module does with this fact (`adr/0149` rule 3's opacity, restated for a third
/// contract riding the same mechanism as <see cref="ModuleQuantityGranted"/>) - it only ever publishes
/// what it itself decided: is this account suspended right now, and until when.</para>
///
/// <para><b><see cref="SuspendedUntil"/> is the *lease's* own instant, not the owner-facing
/// <c>Site.SuspendedUntil</c> value verbatim.</b> A module receiving this fact must fail closed no
/// later than <see cref="SuspendedUntil"/> even if chat itself goes silent forever after - `adr/0166`'s
/// 5-minute lease, renewed at 2.5, is a bound on how *stale* a module's own copy may become, entirely
/// independent of how long the owner chose to suspend the account for. Chat computes this value as
/// "now plus the lease length" each time it publishes (on suspend, on extend, and on every renewal
/// tick <c>Ago.Chat.Worker.SuspensionLeaseRenewalJob</c> runs for as long as the account stays
/// suspended) - never the owner's own <c>suspended_until</c> column, which a module must never see
/// and would tell it nothing it could safely rely on for its own worst-case bound.</para>
///
/// <para><see langword="null"/> means "not suspended" - published once, immediately, the moment an
/// owner lifts a suspension by hand (`adr/0149` rule 1: "chat declining to renew, plus an immediate
/// event that brings the effect forward"). A suspension nobody touches is <em>not</em> announced this
/// way when it lapses on its own - chat simply stops renewing the lease, and every module's own copy
/// fails closed no later than the lease's own remaining length after the last renewal, exactly the
/// bound this contract exists to give a number to.</para>
/// </summary>
public sealed record TenantSuspensionChanged(Guid SiteId, DateTimeOffset? SuspendedUntil, Guid CorrelationId, DateTimeOffset OccurredAt);
