namespace Ago.Chat.Domain;

/// <summary>
/// `13-02`: the pending-intermediate-state row `10-02`/`12-02`'s own planning already anticipated as
/// necessary once a real payment provider entered the picture ("a checkout-session creation and a
/// webhook confirmation are two different moments, and `sites.tier`/`seat_limit` must not change until
/// the second one actually happens" - this item's own Scope). One row per checkout attempt, created
/// <see cref="BillingSubscriptionStatus.Pending"/> the moment ЮKassa hands back a payment id, and moved
/// to a terminal state only by <see cref="BillingWebhookApplier"/>'s own transaction once a real webhook
/// confirms the outcome - never by the checkout-session-creation call itself, which only ever sees a
/// redirect, not a payment (`roadmap.md`'s own wording, restated in `13-02`'s Goal: "once ЮKassa's
/// webhook confirms the payment, never the redirect alone").
///
/// <para><b>Named <c>BillingSubscription</c>, not <c>PendingPayment</c>, even though every row `13-02`
/// itself ever wrote modeled one checkout attempt rather than a recurring cycle</b> - `13-02`'s own
/// Scope named this exact table/type shape (<c>Stage13AddBillingSubscriptions</c>), and the name was
/// the right one for what the row becomes, not only what it started as. <b>`13-03`: this is that
/// promise kept</b> - the recurring re-charge job extends this <i>same row</i> in place rather than
/// inserting a new one per billing cycle (the implementer's-call this item's own Out of scope left
/// open, decided here: one row, mutated across its whole lifetime, the same "one row per aggregate
/// instance" shape every other aggregate in this codebase already uses - a new row per cycle would mean
/// `BillingSubscriptionId` no longer identifies "this subscription" but "this cycle", a rename with no
/// offsetting benefit since nothing in this item's own Scope asks for a queryable per-cycle history and
/// <see cref="BillingWebhookEvent"/> already gives an audit trail of every event ЮKassa ever sent for
/// this row's own payment id).</para>
///
/// <para>Its own aggregate, not folded into <see cref="Site"/> - the identical "does this change
/// independently, in its own transaction, with its own lifecycle" test <see cref="OperatorInvite"/>'s
/// own remarks apply to justify its separation from `Site`. Unlike an invite's redemption, this
/// aggregate's own terminal transitions (first activation, a later renewal, a lapse) *do* also write
/// `Site.Tier`/`Site.SeatLimit` in the same transaction - the same "one wider transaction than the
/// usual one-aggregate rule" shape `RegisterSiteHandler`'s bootstrap and
/// `OperatorInviteRedemptionRepository`'s redemption both already establish, for the identical reason:
/// a partial failure here must not leave a half-applied state (CLAUDE.md rule 4, `10-02`'s own
/// precedent).</para>
/// </summary>
public sealed class BillingSubscription
{
    /// <summary>`13-03`: the fixed length of one billing cycle - a flat 30 days, not a calendar month.
    /// A calendar month has no fixed length (28-31 days), which would make "charge exactly one period
    /// after the last one ended" ambiguous at every month boundary and would make the proration formula
    /// below (`(new_price - old_price) * remaining_days / period_length_days`) depend on which month a
    /// change happened to land in - an implementer's-call this item's own Scope leaves open ("rounded
    /// per whatever rule ЮKassa's own charge API expects - state it"), decided here in favour of the one
    /// value that keeps both the renewal cadence and the proration math simple and consistent
    /// year-round.</summary>
    public static readonly TimeSpan PeriodLength = TimeSpan.FromDays(30);

    /// <summary>`13-03`/`decisions/0006`: "roughly a week" for the failed-recharge retry window, this
    /// item's own stated default (not a measurement, `CLAUDE.md`) - see <see cref="RecordRenewalFailure"/>.</summary>
    public static readonly TimeSpan PastDueRetryWindow = TimeSpan.FromDays(7);

    /// <summary>`13-03`: no more than one charge attempt per calendar day while
    /// <see cref="BillingSubscriptionStatus.PastDue"/> - "daily retries for 7 days" (`decisions/0006`)
    /// stated as a minimum gap between attempts, not a promise the job itself runs exactly once a day
    /// (<see cref="IsRetryDue"/>'s own remarks on why the two are kept separate).</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromDays(1);

    public BillingSubscriptionId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>`23-86`/`adr/0159`: what this row is for - <see langword="null"/> for the one row per
    /// account that is the base (tier and seats, exactly what this type carried before this item), a
    /// real value for every other row, each naming one purchased option. <see cref="RequestedSeats"/>
    /// and <see cref="Tier"/> are meaningless for an option row (zero and empty, set once at
    /// construction and never written again) - an option is priced flat, not by seats
    /// (`adr/0151`'s own price-list reading: "channels at +100 RUB each"), so there is no seat count or
    /// tier band for it to carry. <see cref="IsOption"/>/<see cref="IsBase"/> are this field's own
    /// readable spelling; `GetBillingStatusHandler`'s own remarks (Application) are the one place
    /// getting this distinction wrong would have silently mattered before this item closed it.</summary>
    public BillingOptionKey? OptionKey { get; }

    /// <summary><see langword="true"/> for every row but the account's own base subscription.</summary>
    public bool IsOption => OptionKey is not null;

    /// <summary><see langword="true"/> only for the one row per account `adr/0159` calls the base -
    /// tier and seats, exactly what this type carried before options existed.</summary>
    public bool IsBase => OptionKey is null;

    /// <summary>ЮKassa's own payment id, assigned the moment
    /// <c>IYooKassaPaymentsClient.CreatePaymentAsync</c> returns - the natural key this item's own
    /// webhook applier looks a pending row up by, and half of the idempotency ledger's own composite
    /// key (<see cref="BillingWebhookEvent"/>'s own remarks). Never reassigned by a later renewal
    /// charge - <see cref="LastYooKassaChargeId"/> is where a renewal's own payment id lives.</summary>
    public string YooKassaPaymentId { get; } = string.Empty;

    /// <summary>The seat count this row is currently charging for. `13-02`'s own initial value, set at
    /// checkout; `13-03`'s upgrade path (<see cref="ApplySeatIncreaseImmediately"/>) and renewal path
    /// (<see cref="RecordRenewalSuccess"/>, applying a deferred downgrade) are its only writers past
    /// construction.</summary>
    public int RequestedSeats { get; private set; }

    /// <summary><see cref="SubscriptionTierBands.TryResolveTier"/>'s own output for
    /// <see cref="RequestedSeats"/>, carried on this row rather than re-derived at read time -
    /// <see cref="SubscriptionTierBands"/>'s own band table could in principle change (a future
    /// re-pricing), and this row must reflect the tier actually being charged for, not whatever the
    /// table says today. `13-03`: <c>private set</c>, not the original get-only shape - the same
    /// writers as <see cref="RequestedSeats"/>, always updated alongside it.</summary>
    public string Tier { get; private set; } = string.Empty;

    /// <summary>`25-43`: <see cref="Domain.PublishedPriceVersion.Sequence"/> of the
    /// <see cref="SubscriptionTierBands.BaseSeatPriceKey"/> version this row was actually
    /// charged under, most recently - `0` for an option row (meaningless there, the identical
    /// zero/empty convention <see cref="RequestedSeats"/>/<see cref="Tier"/> already use).
    /// Recorded, never re-derived: a later price change must not be observable through this
    /// row, which is the whole reason this field exists rather than recomputing "the price"
    /// from whatever <see cref="Application.Abstractions.IPriceCatalogRepository"/> says
    /// today. Written alongside <see cref="RequestedSeats"/>/<see cref="Tier"/>, at every
    /// point a real charge is made (<see cref="Create"/>, <see cref="RecordRenewalSuccess"/>,
    /// <see cref="ApplySeatIncreaseImmediately"/>) - never elsewhere, since those are the only
    /// moments a real charge happens.</summary>
    /// <summary>`25-41`: how many Administrator seats beyond the tier-included two this subscription
    /// has bought, at `ago-business` decision `0012`'s flat +500₽/mo each - the identical "purchasable
    /// count, mirroring <see cref="RequestedSeats"/>" shape that field's own remarks already establish,
    /// restated for a flat add-on instead of a banded one. Zero for every row until
    /// <see cref="ApplyAdministratorPurchase"/> is first called, and zero forever for an option row
    /// (<see cref="OptionKey"/>'s own "meaningless for an option row" convention <see cref="RequestedSeats"/>
    /// already uses) - an extra Administrator is priced against the account's own base subscription,
    /// never against a channel/AI option.</summary>
    public int ExtraAdministratorsPurchased { get; private set; }

    /// <summary>`25-41`: the identical "which published version this subscription was actually charged
    /// under" shape <see cref="BaseSeatPriceVersion"/>/<see cref="ExtraSeatPriceVersion"/> already
    /// establish, restated for <see cref="SubscriptionTierBands.AdminExtraPriceKey"/> - so a later
    /// purchase's own proration can read this subscription's own historical figure, never the
    /// catalog's currently-effective one, for the "old" side of the calculation
    /// (`PurchaseAdministratorSlotHandler`'s own remarks give the full reasoning, identical to
    /// `ChangeSubscriptionSeatsHandler`'s). Zero until the first purchase, the same sentinel
    /// <see cref="BaseSeatPriceVersion"/> uses before any seat has ever been charged.</summary>
    public int AdminExtraPriceVersion { get; private set; }

    public int BaseSeatPriceVersion { get; private set; }

    /// <summary>The identical role <see cref="BaseSeatPriceVersion"/> plays, for
    /// <see cref="SubscriptionTierBands.ExtraSeatPriceKey"/>.</summary>
    public int ExtraSeatPriceVersion { get; private set; }

    public BillingSubscriptionStatus Status { get; private set; } = BillingSubscriptionStatus.Pending;

    /// <summary>ЮKassa's own reusable-charge handle, populated only by <see cref="MarkSucceeded"/> -
    /// `null` for every row that is still pending or ended up <see cref="BillingSubscriptionStatus.Failed"/>.
    /// `13-03`'s own recurring charge is this field's real reader; it is never reassigned after the
    /// first payment succeeds - the same handle is presented back to ЮKassa on every future
    /// charge.</summary>
    public string? PaymentMethodId { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>`13-03`: when the period this row is currently paid through ends - `null` until the
    /// first payment succeeds (`13-02`'s own checkout-session creation has no prior period to measure
    /// from), then advanced by <see cref="PeriodLength"/> on every successful renewal
    /// (<see cref="RecordRenewalSuccess"/>). The recurring-charge job's own "is this row due" predicate
    /// (<see cref="IsDueForRenewal"/>).</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; private set; }

    /// <summary>`13-03`: when this row first entered <see cref="BillingSubscriptionStatus.PastDue"/> -
    /// the anchor <see cref="HasExhaustedRetryWindow"/> measures <see cref="PastDueRetryWindow"/> from.
    /// `null` outside <see cref="BillingSubscriptionStatus.PastDue"/>.</summary>
    public DateTimeOffset? PastDueSince { get; private set; }

    /// <summary>`13-03`: when the last renewal/retry charge attempt was made, successful or not - what
    /// <see cref="IsRetryDue"/> gates the "no more than one attempt per day" rule against. Distinct from
    /// <see cref="PastDueSince"/> (which never moves once set) precisely so the two can answer two
    /// different questions: "has the whole window closed" versus "is today's attempt still owed".</summary>
    public DateTimeOffset? LastRenewalAttemptAt { get; private set; }

    /// <summary>`13-03`: an explicit cancellation (<see cref="RequestCancellation"/>) - the recurring
    /// job checks this before ever attempting a charge, matching `decisions/0006`'s "turns off
    /// auto-renewal", never touching <see cref="Status"/> or the site's own entitlements directly. The
    /// paid tier runs until <see cref="CurrentPeriodEnd"/> regardless of when this flag was set.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>`13-03`: a mid-cycle downgrade's own seat count, recorded here rather than applied
    /// immediately (`decisions/0006`'s asymmetric mid-cycle policy) - `null` when no downgrade is
    /// pending. Applied, and cleared, by the next successful <see cref="RecordRenewalSuccess"/>.</summary>
    public int? PendingSeatCount { get; private set; }

    /// <summary>The tier <see cref="PendingSeatCount"/> resolves to - carried alongside it for the
    /// identical reason <see cref="Tier"/> is carried alongside <see cref="RequestedSeats"/>.</summary>
    public string? PendingTier { get; private set; }

    private BillingSubscription(
        BillingSubscriptionId id,
        SiteId siteId,
        string yooKassaPaymentId,
        int requestedSeats,
        string tier,
        int baseSeatPriceVersion,
        int extraSeatPriceVersion,
        BillingSubscriptionStatus status,
        string? paymentMethodId,
        DateTimeOffset createdAt,
        BillingOptionKey? optionKey)
    {
        Id = id;
        SiteId = siteId;
        YooKassaPaymentId = yooKassaPaymentId;
        RequestedSeats = requestedSeats;
        Tier = tier;
        BaseSeatPriceVersion = baseSeatPriceVersion;
        ExtraSeatPriceVersion = extraSeatPriceVersion;
        Status = status;
        PaymentMethodId = paymentMethodId;
        CreatedAt = createdAt;
        OptionKey = optionKey;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private BillingSubscription()
    {
    }

    /// <summary>`25-43`: <paramref name="baseSeatPriceVersion"/>/<paramref name="extraSeatPriceVersion"/>
    /// are the <see cref="Domain.PublishedPriceVersion.Sequence"/> values the caller (`CreateCheckoutSessionHandler`)
    /// actually charged - required, not defaulted, the same "decide, don't default" discipline this
    /// codebase already applies to a real financial fact (`ExpiresAt`'s own remarks on
    /// `EnableModuleForSiteAsOwner` give the identical reasoning for a different field).</summary>
    public static BillingSubscription Create(
        BillingSubscriptionId id, SiteId siteId, string yooKassaPaymentId, int requestedSeats, string tier,
        int baseSeatPriceVersion, int extraSeatPriceVersion, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(yooKassaPaymentId))
        {
            throw new ArgumentException("ЮKassa payment id cannot be empty.", nameof(yooKassaPaymentId));
        }

        return new BillingSubscription(
            id, siteId, yooKassaPaymentId, requestedSeats, tier, baseSeatPriceVersion, extraSeatPriceVersion,
            BillingSubscriptionStatus.Pending, paymentMethodId: null, createdAt, optionKey: null);
    }

    /// <summary>`23-86`/`adr/0159`: the account's base subscription mints through <see cref="Create"/>
    /// with <see cref="RequestedSeats"/>/<see cref="Tier"/> a real seat count and tier band; an option
    /// has neither, so this factory takes only what an option row actually carries -
    /// <paramref name="optionKey"/> in place of both, <see cref="RequestedSeats"/>/<see cref="Tier"/>
    /// fixed at zero/empty (never written again - see this type's own <see cref="OptionKey"/> remarks).
    /// `25-43`: <see cref="BaseSeatPriceVersion"/>/<see cref="ExtraSeatPriceVersion"/> are meaningless
    /// for an option row for the identical reason - an option is priced flat, not by seats, so there is
    /// no seat-pricing key for either to name - fixed at `0` here, the same convention.
    /// A separate factory rather than an optional trailing parameter on <see cref="Create"/> - the same
    /// "a materially different construction gets its own named entry point, not a flag" judgement this
    /// codebase already applies elsewhere (`EnableModuleForSiteAsOwner` alongside `EnableModuleForSite`,
    /// Application), so a caller reading `CreateOption(...)` at a call site does not have to also read
    /// every other parameter's default to know what kind of row it is minting.</summary>
    public static BillingSubscription CreateOption(
        BillingSubscriptionId id, SiteId siteId, string yooKassaPaymentId, BillingOptionKey optionKey, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(yooKassaPaymentId))
        {
            throw new ArgumentException("ЮKassa payment id cannot be empty.", nameof(yooKassaPaymentId));
        }

        return new BillingSubscription(
            id, siteId, yooKassaPaymentId, requestedSeats: 0, tier: string.Empty, baseSeatPriceVersion: 0,
            extraSeatPriceVersion: 0, BillingSubscriptionStatus.Pending, paymentMethodId: null, createdAt, optionKey);
    }

    /// <summary>Applied by <see cref="BillingWebhookApplier"/> on a verified, first-seen
    /// <c>payment.succeeded</c> event, in the same transaction as <see cref="Site.ActivateSubscription"/>.
    /// Throws on an already-terminal row rather than silently overwriting it - the idempotency ledger
    /// (<see cref="BillingWebhookEvent"/>) is what is supposed to stop a redelivered event from ever
    /// reaching this call a second time; reaching this guard at all means that ledger check was bypassed,
    /// the same "last line of defence for a caller that skipped the pre-check" shape
    /// <see cref="OperatorInvite.Redeem"/>'s own guard describes.
    ///
    /// <para>`13-03`: also sets <see cref="CurrentPeriodEnd"/> to <paramref name="now"/> +
    /// <see cref="PeriodLength"/> - the first period this row is ever paid through, and the anchor every
    /// later renewal advances from.</para>
    ///
    /// <para>`23-86`/`adr/0159`: for an option (<see cref="IsOption"/>), <paramref name="alignedPeriodEnd"/>
    /// is required and overrides that default - "an option's <see cref="CurrentPeriodEnd"/> is copied
    /// from the base at purchase... so both renew on the same date" (`adr/0159`'s own Decision), never
    /// a fresh <paramref name="now"/> + <see cref="PeriodLength"/> of its own. Enforced here, not left to
    /// a caller to remember: a base row given an explicit override, or an option row given none, is a
    /// caller bug this constructor-adjacent guard catches before a period ever drifts out of alignment -
    /// the identical "there is no such thing as a validated-somewhere-else entity" posture
    /// <see cref="EnabledModule"/>'s own expiry check applies for a different invariant.</para></summary>
    public void MarkSucceeded(string? paymentMethodId, DateTimeOffset now, DateTimeOffset? alignedPeriodEnd = null)
    {
        if (Status != BillingSubscriptionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is already {Status} and cannot be marked succeeded again.");
        }

        if (IsOption && alignedPeriodEnd is null)
        {
            throw new ArgumentException(
                "An option subscription's period must be aligned to the account's base subscription.", nameof(alignedPeriodEnd));
        }

        if (IsBase && alignedPeriodEnd is not null)
        {
            throw new ArgumentException(
                "Only an option subscription's period is aligned to another row - a base subscription starts its own.",
                nameof(alignedPeriodEnd));
        }

        Status = BillingSubscriptionStatus.Succeeded;
        PaymentMethodId = paymentMethodId;
        CurrentPeriodEnd = alignedPeriodEnd ?? now + PeriodLength;
    }

    /// <summary>Applied on a verified, first-seen <c>payment.canceled</c> event -
    /// <see cref="Site.Tier"/>/<see cref="Site.SeatLimit"/> are never touched by this path (`13-02`'s
    /// own Scope: "they were never changed from free in the first place, since the pending row - not
    /// the site - held the in-flight state"), so this method's only job is recording the outcome on
    /// this row.</summary>
    public void MarkFailed()
    {
        if (Status != BillingSubscriptionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is already {Status} and cannot be marked failed again.");
        }

        Status = BillingSubscriptionStatus.Failed;
    }

    /// <summary>`13-03`: is this row due for a renewal attempt right now - a currently-<see cref="BillingSubscriptionStatus.Succeeded"/>
    /// row whose <see cref="CurrentPeriodEnd"/> has passed. <see langword="false"/> for every other
    /// status, including <see cref="BillingSubscriptionStatus.PastDue"/> - that row's own due-ness is
    /// <see cref="IsRetryDue"/>'s question, not this one's, because the two have different cadences (one
    /// shot at the period boundary versus once a day inside the retry window).</summary>
    public bool IsDueForRenewal(DateTimeOffset now) =>
        Status == BillingSubscriptionStatus.Succeeded && CurrentPeriodEnd is { } periodEnd && now >= periodEnd;

    /// <summary>`13-03`: is a <see cref="BillingSubscriptionStatus.PastDue"/> row owed today's retry
    /// attempt - <see langword="true"/> the first time this row is seen <see cref="BillingSubscriptionStatus.PastDue"/>
    /// (<see cref="LastRenewalAttemptAt"/> still holds whatever it was before, from the renewal attempt
    /// that itself caused the failure) or once at least <see cref="RetryInterval"/> has passed since the
    /// last attempt. Deliberately gated on elapsed time rather than a plain attempt counter - the
    /// recurring-charge job's own tick interval is allowed to run more often than once a day (matching
    /// every other Worker job's "check often, act rarely" shape), and this is what stops a sub-daily
    /// tick from charging more than once inside a single day.</summary>
    public bool IsRetryDue(DateTimeOffset now) =>
        Status == BillingSubscriptionStatus.PastDue
        && (LastRenewalAttemptAt is not { } lastAttempt || now - lastAttempt >= RetryInterval);

    /// <summary>`13-03`: has the 7-day <see cref="PastDueRetryWindow"/> closed with no successful retry -
    /// the recurring-charge job's own "give up and lapse this row" predicate. Only meaningful while
    /// <see cref="BillingSubscriptionStatus.PastDue"/>.</summary>
    public bool HasExhaustedRetryWindow(DateTimeOffset now) =>
        Status == BillingSubscriptionStatus.PastDue
        && PastDueSince is { } pastDueSince
        && now - pastDueSince >= PastDueRetryWindow;

    /// <summary>`13-03`: a scheduled re-charge against <see cref="PaymentMethodId"/> failed -
    /// <see cref="BillingSubscriptionStatus.Succeeded"/> -&gt; <see cref="BillingSubscriptionStatus.PastDue"/>.
    /// `Site.Tier`/`Site.SeatLimit` stay exactly as they are - the caller (the recurring-charge job)
    /// must not touch them on this path, matching `decisions/0006`'s "full access retained".</summary>
    public void RecordRenewalFailure(DateTimeOffset now)
    {
        if (Status != BillingSubscriptionStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status}, not Succeeded, and cannot record a renewal failure.");
        }

        Status = BillingSubscriptionStatus.PastDue;
        PastDueSince = now;
        LastRenewalAttemptAt = now;
    }

    /// <summary>`13-03`: a retry inside the window failed again - stays
    /// <see cref="BillingSubscriptionStatus.PastDue"/>, only <see cref="LastRenewalAttemptAt"/> moves.
    /// <see cref="PastDueSince"/> is never touched - it anchors the whole 7-day window, not any one
    /// attempt inside it.</summary>
    public void RecordRenewalRetryFailure(DateTimeOffset now)
    {
        if (Status != BillingSubscriptionStatus.PastDue)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status}, not PastDue, and cannot record a retry failure.");
        }

        LastRenewalAttemptAt = now;
    }

    /// <summary>`13-03`: a renewal or retry charge succeeded - from either
    /// <summary>`13-03`: a renewal or retry charge succeeded - from either
    /// <see cref="BillingSubscriptionStatus.Succeeded"/> (an on-time renewal) or
    /// <see cref="BillingSubscriptionStatus.PastDue"/> (a retry inside the window) back to
    /// <see cref="BillingSubscriptionStatus.Succeeded"/>, <see cref="CurrentPeriodEnd"/> advanced by one
    /// <see cref="PeriodLength"/> from itself (not from <paramref name="now"/> - a late-paying retry
    /// must not shorten the next period just because it landed a few days into the grace window), and
    /// the <see cref="PastDue"/>-tracking fields cleared.
    ///
    /// <para>Also where a pending downgrade (<see cref="PendingSeatCount"/>/<see cref="PendingTier"/>)
    /// actually takes effect, per `decisions/0006`'s "downgrades apply at the next renewal" - applied
    /// and cleared here, in the one place a renewal is known to have genuinely happened, rather than at
    /// the period boundary alone (a boundary a failed/retried charge may cross more than once before a
    /// renewal actually succeeds).</para>
    ///
    /// <para>`25-43`: <see cref="BaseSeatPriceVersion"/>/<see cref="ExtraSeatPriceVersion"/> are
    /// overwritten unconditionally on every successful renewal, whether or not a seat count actually
    /// changed - the item's own "a tenant mid-renewal when the price changes is not a special case":
    /// the renewal always recomputes and charges the currently-effective price, so this row's own
    /// record of "what it was last charged under" must always move with it, not only when
    /// <see cref="PendingSeatCount"/> also happened to apply. The caller (`ProcessSubscriptionRenewalHandler`)
    /// resolved these from <see cref="Application.Abstractions.IPriceCatalogRepository"/> immediately
    /// before making the actual charge this call is recording the success of.</para></summary>
    public void RecordRenewalSuccess(DateTimeOffset now, string? paymentMethodId, int baseSeatPriceVersion, int extraSeatPriceVersion)
    {
        if (Status is not (BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue))
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status} and cannot record a renewal success.");
        }

        Status = BillingSubscriptionStatus.Succeeded;
        if (!string.IsNullOrEmpty(paymentMethodId))
        {
            PaymentMethodId = paymentMethodId;
        }

        CurrentPeriodEnd = (CurrentPeriodEnd ?? now) + PeriodLength;
        PastDueSince = null;
        LastRenewalAttemptAt = now;
        BaseSeatPriceVersion = baseSeatPriceVersion;
        ExtraSeatPriceVersion = extraSeatPriceVersion;

        if (PendingSeatCount is { } pendingSeats && PendingTier is { } pendingTier)
        {
            RequestedSeats = pendingSeats;
            Tier = pendingTier;
            PendingSeatCount = null;
            PendingTier = null;
        }
    }

    /// <summary>`13-03`: the 7-day retry window closed with nothing recovered, or a cancelled
    /// subscription reached its own paid-through period end with no charge ever attempted - either way,
    /// this row no longer entitles anything. The caller (the recurring-charge job) downgrades the site
    /// to `tier='free'`/`seat_limit=1` in the same transaction, the same write path
    /// <see cref="Site.ActivateSubscription"/> already provides.</summary>
    public void MarkLapsed()
    {
        if (Status is not (BillingSubscriptionStatus.PastDue or BillingSubscriptionStatus.Succeeded))
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status} and cannot lapse.");
        }

        Status = BillingSubscriptionStatus.Lapsed;
    }

    /// <summary>`13-03`/`decisions/0006`: "turns off auto-renewal and leaves the paid tier running
    /// until the end of the period already paid for. No refund." A flag, not an immediate write - the
    /// recurring-charge job is what actually acts on it, at <see cref="CurrentPeriodEnd"/>. Idempotent
    /// re-cancellation is deliberately allowed (unlike every other transition here) - there is no
    /// meaningful "already cancelled" error a caller needs surfaced, only two states
    /// (<see langword="true"/>/<see langword="false"/>) with no history to protect.</summary>
    public void RequestCancellation(DateTimeOffset now)
    {
        if (Status is not (BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue))
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status} and cannot be cancelled.");
        }

        CancelRequested = true;
    }

    /// <summary>`13-03`/`decisions/0006`: "upgrades apply immediately and the difference for the
    /// <summary>`13-03`/`decisions/0006`: "upgrades apply immediately and the difference for the
    /// remainder of the period is charged at once" - called only after that prorated charge has already
    /// succeeded (the caller's own job, mirroring `13-02`'s "verified success, not the redirect alone"
    /// discipline). <see cref="RequestedSeats"/>/<see cref="Tier"/> move immediately; there is nothing
    /// to defer. `25-43`: <see cref="BaseSeatPriceVersion"/>/<see cref="ExtraSeatPriceVersion"/> move
    /// with them - the caller already resolved the new price from
    /// <see cref="Application.Abstractions.IPriceCatalogRepository"/> to compute the very proration
    /// this call is applying the outcome of.</summary>
    public void ApplySeatIncreaseImmediately(int newSeatCount, string newTier, int baseSeatPriceVersion, int extraSeatPriceVersion)
    {
        if (Status != BillingSubscriptionStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status}, not Succeeded, and cannot change its seat count.");
        }

        if (newSeatCount <= RequestedSeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newSeatCount), newSeatCount, "An immediate seat change must increase the seat count - a decrease is a deferred change.");
        }

        RequestedSeats = newSeatCount;
        Tier = newTier;
        BaseSeatPriceVersion = baseSeatPriceVersion;
        ExtraSeatPriceVersion = extraSeatPriceVersion;
    }

    /// <summary>`25-41`: the identical "applies immediately, already-charged" shape
    /// <see cref="ApplySeatIncreaseImmediately"/> establishes for seats, restated for a flat
    /// Administrator-slot purchase - there is no deferred-decrease counterpart
    /// (<see cref="ScheduleSeatDecrease"/>'s own sibling) because this item's own Scope never asks for
    /// a self-service way to buy fewer: the only way this count ever goes down is the automatic
    /// demotion a lapse/downgrade triggers (`IAdministratorLimitEnforcer`'s own remarks), which acts on
    /// <see cref="Site.AdminLimit"/> directly and never calls back into this method to record a lower
    /// count here - the same "the subscription row itself is never mutated by a lapse it did not
    /// choose to record a decrease for" posture <see cref="MarkLapsed"/>'s own remarks already
    /// establish for <see cref="RequestedSeats"/>/<see cref="Tier"/> (neither is reset there
    /// either).</summary>
    public void ApplyAdministratorPurchase(int newExtraAdministratorCount, int adminExtraPriceVersion)
    {
        if (Status != BillingSubscriptionStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status}, not Succeeded, and cannot purchase an extra Administrator seat.");
        }

        if (newExtraAdministratorCount <= ExtraAdministratorsPurchased)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newExtraAdministratorCount), newExtraAdministratorCount,
                "Purchasing an extra Administrator seat must increase the count - this codebase has no way to buy fewer on its own.");
        }

        ExtraAdministratorsPurchased = newExtraAdministratorCount;
        AdminExtraPriceVersion = adminExtraPriceVersion;
    }

    /// <summary>`13-03`/`decisions/0006`: "downgrades apply at the next renewal, with no credit for
    /// unused time" - recorded here, applied by <see cref="RecordRenewalSuccess"/>. Never blocked by the
    /// site's live operator count (`decisions/0006`'s own rejection of that alternative) - a downgrade
    /// that will leave the site over-seats is exactly the derived condition this item's own Scope names,
    /// not a reason to refuse the downgrade itself.</summary>
    public void ScheduleSeatDecrease(int newSeatCount, string newTier)
    {
        if (Status is not (BillingSubscriptionStatus.Succeeded or BillingSubscriptionStatus.PastDue))
        {
            throw new InvalidOperationException(
                $"Billing subscription {Id.Value} is {Status} and cannot schedule a seat change.");
        }

        if (newSeatCount >= RequestedSeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newSeatCount), newSeatCount, "A scheduled seat change must decrease the seat count - an increase is an immediate change.");
        }

        PendingSeatCount = newSeatCount;
        PendingTier = newTier;
    }
}
