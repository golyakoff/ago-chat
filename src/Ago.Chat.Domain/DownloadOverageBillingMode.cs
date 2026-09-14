namespace Ago.Chat.Domain;

/// <summary>
/// `25-84`: the platform-owner-only, per-tenant toggle that decides *when* a tenant commits to paying
/// the metered download-overage charge - never *whether* (`docs/backlog/25-84-*.md`'s own decision:
/// "both modes ultimately charge the same 100 ₽/GB meter; the toggle decides when the tenant commits
/// to paying it").
///
/// <para><b><see cref="Manual"/> is `0`, and therefore the column default every existing row keeps.</b>
/// `docs/backlog/25-84-*.md` calls <see cref="AutoBill"/> "the recommended default", and that is a
/// recommendation to the platform owner about what to *set*, not a licence for a migration to flip
/// every tenant that already exists onto a silent, unagreed recurring charge. A tenant on
/// <see cref="Manual"/> behaves exactly as `25-83` shipped - blocked at the hard threshold, nothing
/// charged - so the migration that adds this column changes nobody's bill, which is the only honest
/// direction for a default on a money switch. The alternative (default <see cref="AutoBill"/>) would
/// have every live tenant silently start accruing a charge the moment this ships, with the first
/// notice arriving on an invoice.</para>
/// </summary>
public enum DownloadOverageBillingMode
{
    /// <summary>Crossing the hard threshold blocks exactly as `25-83`'s own base case describes, until
    /// the tenant completes a real checkout for the overage accrued so far
    /// (<c>PurchaseDownloadOverageHandler</c>). That purchase unblocks them for the remainder of the
    /// current calendar month and opts them into the same metered accrual <see cref="AutoBill"/> has
    /// from the start - see <see cref="DownloadOveragePricing"/>'s own remarks for why "pay once, then
    /// meter" is the only reading of `25-84` that satisfies all of its own sentences at once.</summary>
    Manual = 0,

    /// <summary>Crossing the hard threshold charges the per-gigabyte meter as it accrues and keeps the
    /// tenant unblocked, with the accumulated amount settled onto their next regular renewal charge
    /// (<c>ProcessSubscriptionRenewalHandler</c>). No action from the tenant at the moment of crossing.
    /// Bounded by `Ago.Chat.Application.Abstractions.DownloadThresholds.AutoBillCapRub` (named in prose,
    /// not a `see cref`: Domain references nothing, least of all Application) - see that property's own
    /// remarks for why this item decided a second ceiling is warranted.</summary>
    AutoBill = 1,
}
