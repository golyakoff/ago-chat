using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-03`: the recurring-charge job's own commit step, called by
/// <c>Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler</c>
/// only after any outbound ЮKassa call the branch needed has already returned - the identical "charge
/// first, commit the verified outcome second, never the other way round" discipline `13-02`'s own
/// checkout path and webhook applier both already establish. Every method here reloads the row fresh
/// inside its own transaction rather than trusting whatever the handler's own earlier read saw - the
/// handler's read only ever decides <i>which</i> outbound call (if any) to make; the transaction is
/// what actually decides state, so it must not act on a snapshot that may be a Worker tick old.
/// </summary>
public interface ISubscriptionRenewalApplier
{
    /// <summary>The 7-day retry window closed with nothing recovered, or a cancelled subscription
    /// reached its own paid-through period end - <see cref="BillingSubscription.MarkLapsed"/> plus the
    /// same-transaction downgrade to `tier='free'`/`seat_limit=1` (<see cref="Site.ActivateSubscription"/>).
    /// No outbound call precedes this - `decisions/0006`'s "no charge attempt, successful or otherwise".</summary>
    Task ApplyLapseAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A renewal or retry charge succeeded - <see cref="BillingSubscription.RecordRenewalSuccess"/>,
    /// and, only if that call actually applied a pending deferred downgrade, the matching same-transaction
    /// `Site.Tier`/`Site.SeatLimit` write. No new <c>payment_method_id</c> to record - a charge-on-file
    /// call reuses the one already stored and ЮKassa's own response carries no replacement, unlike
    /// `13-02`'s first-payment webhook.
    /// <para>`25-43`: <paramref name="baseSeatPriceVersion"/>/<paramref name="extraSeatPriceVersion"/>
    /// are the price-catalog sequence numbers the caller (`ProcessSubscriptionRenewalHandler`)
    /// actually charged this renewal against, passed straight to
    /// <see cref="BillingSubscription.RecordRenewalSuccess"/> - meaningless for an option row,
    /// which passes `0`/`0` (the identical zero convention its own `RequestedSeats`/`Tier`
    /// already use).</para></summary>
    /// <para>`25-84`: <paramref name="overageSettlements"/> is the download-overage the charge that just
    /// succeeded actually carried - written as <see cref="Domain.DownloadOverageCharge.SettledOnInvoice"/>
    /// rows in this same transaction, so "the money moved" and "the ledger says so" commit together or
    /// not at all. Empty for the overwhelming majority of renewals; never <see langword="null"/>.</para>
    Task ApplyRenewalSuccessAsync(
        BillingSubscriptionId id, DateTimeOffset now, int baseSeatPriceVersion, int extraSeatPriceVersion,
        IReadOnlyList<DownloadOverageInvoiceLine> overageSettlements,
        CancellationToken cancellationToken);

    /// <summary>A renewal or retry charge was refused - <see cref="BillingSubscription.RecordRenewalFailure"/>
    /// (first failure, from <c>Succeeded</c>) or <see cref="BillingSubscription.RecordRenewalRetryFailure"/>
    /// (a later retry, already <c>PastDue</c>), decided fresh from the row's own current status inside this
    /// transaction. `Site.Tier`/`Site.SeatLimit` are never touched on this path.</summary>
    Task ApplyRenewalFailureAsync(BillingSubscriptionId id, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// `25-84`: one calendar month's worth of download overage, as it appears on one renewal charge.
///
/// <para><b>This is the "line item" `docs/backlog/25-84-*.md` asks for, and it is worth saying plainly
/// what that means here: this codebase has no invoice entity.</b> A "regular invoice" in AGO Chat is a
/// single ЮKassa charge-on-file payment with a description string
/// (<c>ProcessSubscriptionRenewalHandler</c>). So an overage line is realised as three real, sourced
/// things and no invented fourth one: the renewal's own <c>AmountRub</c> increases by
/// <see cref="AmountRub"/>, the payment's own description names it, and this row lands in
/// <c>download_overage_charges</c> as the durable, per-month, per-price-version record a tenant could
/// be shown when they ask. Inventing an `Invoice`/`InvoiceLine` aggregate to satisfy the word "line
/// item" would have been a second billing model beside the one that actually charges money - the
/// failure `25-23`'s own "a real, sourced figure on the wire, never invented client-side" discipline
/// exists to prevent, applied to the server side.</para>
/// </summary>
/// <param name="PeriodMonth">The calendar month these bytes were downloaded in - not the renewal's own
/// period, which uses a different boundary entirely (`23-82`'s own `period_month` bucketing is calendar
/// months; a subscription period is 30 days from whenever it activated). A renewal therefore settles
/// whole calendar months, possibly more than one, never "the period just ended".</param>
/// <param name="OutstandingBytes">Bytes past the hard threshold this line pays for.</param>
/// <param name="AmountRub">What <paramref name="OutstandingBytes"/> costs at
/// <paramref name="PriceVersion"/>'s own published figure.</param>
/// <param name="PriceVersion">The <c>published_price_versions.sequence</c> the amount was computed
/// from - stored so the figure stays explainable after the price changes.</param>
public sealed record DownloadOverageInvoiceLine(
    DateOnly PeriodMonth, long OutstandingBytes, decimal AmountRub, int PriceVersion);
