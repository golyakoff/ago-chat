namespace Ago.Chat.Domain;

/// <summary>
/// `25-84`: one attempt to turn some number of bytes-over-the-hard-threshold into money, for one site
/// in one calendar month. <b>Append-only</b> - a month's settled position is the sum of its
/// <see cref="DownloadOverageChargeStatus.Succeeded"/> rows, never a mutable "how much is settled"
/// column somebody has to keep correct. That is the same shape <see cref="BillingWebhookEvent"/>'s own
/// ledger and <c>WebhookDelivery</c> already use, chosen here for a third reason of its own: this is
/// the table a tenant would point at when disputing an invoice line, and a ledger answers "what was
/// charged, when, at what price, against what byte count" while a running total answers none of it.
///
/// <para><b>Two sources, one table.</b> A <c>Checkout</c> row is a tenant's own explicit purchase on
/// the <see cref="DownloadOverageBillingMode.Manual"/> path - created <see cref="DownloadOverageChargeStatus.Pending"/>
/// by <c>PurchaseDownloadOverageHandler</c> and moved to <see cref="DownloadOverageChargeStatus.Succeeded"/>
/// only by a verified ЮKassa webhook (<c>BillingWebhookApplier</c>), never by the browser's return from
/// the hosted checkout page - `13-02`'s own "never the redirect alone", restated for a second kind of
/// purchase. An <c>Invoice</c> row is the <see cref="DownloadOverageBillingMode.AutoBill"/> path's own
/// settlement, written already-<see cref="DownloadOverageChargeStatus.Succeeded"/> by
/// <c>SubscriptionRenewalApplier</c> in the same transaction that records the renewal whose charge
/// actually carried it. Keeping both in one table is what makes "how much of this month is settled" a
/// single query rather than a union the gate would have to run on every blocked download.</para>
///
/// <para><b>The paid-checkout row is also the manual path's own unblock.</b> `docs/backlog/25-84-*.md`
/// requires a payment to unblock "the tenant's next presigned GET... without waiting for any billing
/// cycle boundary", and requires that unblock to expire at the end of the calendar month with no
/// rollover. A <c>Checkout</c> row already carries both facts - it exists, and it is stamped with a
/// <see cref="PeriodMonth"/> - so no separate "unblocked until" column is needed, and no job has to
/// remember to expire one. The month rolls over; the row stops matching; the block returns. See
/// <c>GetAttachmentDownloadUrlHandler.EnforceDownloadCapAsync</c> for the read.</para>
/// </summary>
public sealed class DownloadOverageCharge
{
    public DownloadOverageChargeId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>The first day of the calendar month this charge covers - the identical bucket key
    /// `site_attachment_egress.period_month` already uses (`23-82`), so the two join on equality with
    /// no date arithmetic anywhere.</summary>
    public DateOnly PeriodMonth { get; }

    public DownloadOverageChargeSource Source { get; }

    public DownloadOverageChargeStatus Status { get; private set; }

    /// <summary>How many bytes past the hard threshold this particular charge covers - the marginal
    /// amount, not the cumulative one, so a month's settled byte position is a plain
    /// <c>SUM(bytes_over)</c> over the succeeded rows.</summary>
    public long BytesOver { get; }

    public decimal AmountRub { get; }

    /// <summary><c>PublishedPriceVersion.Sequence</c> for <see cref="DownloadOveragePricing.OveragePerGigabyteKey"/>
    /// at the moment this charge was computed - the identical grandfathering discipline
    /// <see cref="BillingSubscription.BaseSeatPriceVersion"/> keeps, and for the identical reason: a
    /// tenant asking "why is this line 340 ₽" must be answerable from the row itself, not from whatever
    /// price happens to be current when they ask.</summary>
    public int PriceVersion { get; }

    /// <summary>ЮKassa's own payment id for a <see cref="DownloadOverageChargeSource.Checkout"/> row -
    /// <see langword="null"/> for an <see cref="DownloadOverageChargeSource.Invoice"/> row, whose money
    /// moved as part of the renewal payment, not as a payment of its own.</summary>
    public string? YooKassaPaymentId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? SettledAt { get; private set; }

    private DownloadOverageCharge(
        DownloadOverageChargeId id,
        SiteId siteId,
        DateOnly periodMonth,
        DownloadOverageChargeSource source,
        DownloadOverageChargeStatus status,
        long bytesOver,
        decimal amountRub,
        int priceVersion,
        string? yooKassaPaymentId,
        DateTimeOffset createdAt,
        DateTimeOffset? settledAt)
    {
        Id = id;
        SiteId = siteId;
        PeriodMonth = periodMonth;
        Source = source;
        Status = status;
        BytesOver = bytesOver;
        AmountRub = amountRub;
        PriceVersion = priceVersion;
        YooKassaPaymentId = yooKassaPaymentId;
        CreatedAt = createdAt;
        SettledAt = settledAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private DownloadOverageCharge()
    {
    }

    /// <summary>The manual path's own purchase, before ЮKassa has confirmed anything -
    /// <see cref="DownloadOverageChargeStatus.Pending"/>, and deliberately not yet an unblock. See this
    /// type's own remarks on why only a verified webhook may promote it.</summary>
    public static DownloadOverageCharge PendingCheckout(
        DownloadOverageChargeId id,
        SiteId siteId,
        DateOnly periodMonth,
        long bytesOver,
        decimal amountRub,
        int priceVersion,
        string yooKassaPaymentId,
        DateTimeOffset createdAt) =>
        new(id, siteId, periodMonth, DownloadOverageChargeSource.Checkout, DownloadOverageChargeStatus.Pending,
            bytesOver, amountRub, priceVersion, yooKassaPaymentId, createdAt, settledAt: null);

    /// <summary>The auto-bill path's own settlement, written by the renewal that actually carried the
    /// money - born <see cref="DownloadOverageChargeStatus.Succeeded"/> because the charge it records
    /// has already been made by the time this row is created (the identical "charge first, commit the
    /// verified outcome second" ordering `13-03`'s own renewal path already follows).</summary>
    public static DownloadOverageCharge SettledOnInvoice(
        DownloadOverageChargeId id,
        SiteId siteId,
        DateOnly periodMonth,
        long bytesOver,
        decimal amountRub,
        int priceVersion,
        DateTimeOffset settledAt) =>
        new(id, siteId, periodMonth, DownloadOverageChargeSource.Invoice, DownloadOverageChargeStatus.Succeeded,
            bytesOver, amountRub, priceVersion, yooKassaPaymentId: null, createdAt: settledAt, settledAt: settledAt);

    /// <summary>ЮKassa confirmed the checkout payment. Idempotent by construction at the caller
    /// (<c>BillingWebhookApplier</c>'s own ledger already refuses a redelivered event before reaching
    /// here), and harmless if reached twice regardless - the same terminal value written twice.</summary>
    public void MarkSucceeded(DateTimeOffset now)
    {
        Status = DownloadOverageChargeStatus.Succeeded;
        SettledAt = now;
    }

    /// <summary>ЮKassa cancelled or refused the checkout payment. The row stays, failed - a tenant who
    /// abandons a hosted checkout page leaves a record of having tried, which is worth more when
    /// somebody later asks why they were still blocked than a deleted row would be.</summary>
    public void MarkFailed(DateTimeOffset now)
    {
        Status = DownloadOverageChargeStatus.Failed;
        SettledAt = now;
    }
}

/// <summary>Which of `25-84`'s two paths produced this row - see <see cref="DownloadOverageCharge"/>'s
/// own remarks. Stored as its member name, not its ordinal (<c>DownloadOverageChargeConfiguration</c>),
/// the same convention <c>BillingSubscriptionStatus</c> already uses.</summary>
public enum DownloadOverageChargeSource
{
    /// <summary>The tenant's own explicit purchase on the <see cref="DownloadOverageBillingMode.Manual"/>
    /// path.</summary>
    Checkout = 0,

    /// <summary>A settlement swept onto a regular renewal charge on the
    /// <see cref="DownloadOverageBillingMode.AutoBill"/> path.</summary>
    Invoice = 1,
}

/// <summary>The same three-state shape <c>BillingSubscriptionStatus</c> uses for the identical
/// "created pending, confirmed or refused by a webhook" lifecycle.</summary>
public enum DownloadOverageChargeStatus
{
    Pending = 0,

    Succeeded = 1,

    Failed = 2,
}
