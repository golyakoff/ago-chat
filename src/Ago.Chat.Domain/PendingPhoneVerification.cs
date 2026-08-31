using System.Security.Cryptography;

namespace Ago.Chat.Domain;

/// <summary>
/// `14-15`/`adr/0079`: the sibling of <see cref="PendingChannelLinkRequest"/> for a channel that cannot
/// supply `14-12`'s own inbound evidence - see that item's own backlog file, "Why this cannot reuse
/// 14-12's mechanism". Its own aggregate, its own lifecycle (issued, confirmed once, locked out, or left
/// to expire unconfirmed), for the identical reasons <see cref="PendingChannelLinkRequest"/> is its own
/// aggregate rather than a value object on <see cref="Visitor"/>.
///
/// <para><b>On a confirmed code: the outcome is a real <see cref="ChannelIdentity"/>, never a parallel
/// "verified phone" concept.</b> This item's own backlog file states the reasoning in full ("Why not a
/// parallel concept"); this type's only job is to produce the evidence <see cref="ChannelIdentity.Link"/>
/// already knows how to consume - it holds no visitor-facing trust state of its own once
/// <see cref="AttemptConfirm"/> returns <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>.</para>
///
/// <para><b>Why <see cref="ChannelKind.Sms"/> is what the resulting identity is tagged with, whichever
/// <see cref="PhoneVerificationDeliveryMethod"/> actually delivered the code.</b> The identity being
/// proven is "this phone number, reachable by this visitor" - a PSTN address - not "which wire carried the
/// six digits". Reusing the existing, currently-unused <see cref="ChannelKind.Sms"/> member (unused since
/// `14-03`'s own SMS <em>channel adapter</em> - full two-way messaging - went `won't build`) does not
/// misrepresent capability: this codebase's own <c>InboundChannelAdapterRegistry</c> has no adapter
/// registered for <see cref="ChannelKind.Sms"/> today, so <c>DeliverChannelMessageHandler</c> already
/// degrades a resolved-but-unadapted channel to its existing "no adapter for this channel" outcome
/// (<c>DeliverChannelMessage.NoAdapter</c>'s own remarks) rather than crashing or silently attempting a
/// send - the identical graceful-degradation path any other unbuilt-adapter channel identity would hit. A
/// dedicated <c>ChannelKind.Phone</c> member was considered and rejected: it would suggest two different
/// "channels" exist for one phone number depending on how it was verified, which is the wrong axis
/// entirely - the axis that matters to every reader of <see cref="ChannelIdentity"/> is "can this be
/// reached on the phone network", not "was the six-digit proof read aloud or texted".</para>
///
/// <para><b>Lockout, not just expiry - the one real structural difference from
/// <see cref="PendingChannelLinkRequest"/>.</b> That type never needed an attempt counter because its own
/// confirmation path is passive - "does an inbound message happen to match a live code" - never an
/// interactive "submit a guess" endpoint a script could hammer. This type's own confirmation is exactly
/// that interactive endpoint (a visitor typing a code into a form), so unlike that sibling, guessing here
/// costs an attacker nothing but time unless something counts attempts. <see cref="AttemptCount"/> plus
/// <see cref="MaxAttempts"/> is that count - the same defense-in-depth reasoning the backlog item's own
/// Scope section names, "the same rate limiting `visitor-sessions` already applies elsewhere".</para>
///
/// <para><b><see cref="CodeHash"/>, hashed for the identical reason and with the identical caveat
/// <see cref="PendingChannelLinkRequest.CodeHash"/>'s own remarks give</b>: deliberately low entropy (a
/// human reads it off a phone call or an SMS and types it back), so the hash buys no brute-force
/// resistance a determined attacker with the row in hand could not already get - the real security here
/// is <see cref="MaxAttempts"/> plus the short <see cref="ExpiresAt"/> window, and the hash is still
/// applied for the uniform "never store a bearer-shaped value in plaintext" discipline.</para>
///
/// <para><b>Why <see cref="AttemptConfirm"/> returns a <see cref="PhoneVerificationConfirmOutcome"/>
/// rather than throwing, unlike <see cref="PendingChannelLinkRequest.Consume"/>.</b> That method's own
/// remarks are explicit that its guards exist only to catch a genuine race - its caller only ever reaches
/// it after <c>FindLiveAsync</c> already filtered to a row that is live, so "already consumed"/"expired"
/// there really is a caller bug or a lost race, worth throwing over. This type's own caller
/// (<c>ConfirmPhoneVerificationHandler</c>) instead loads a specific row by id, unfiltered, because the
/// whole point of this Done-when is that a wrong code, an expired code, and a lockout are each a distinct,
/// expected, everyday outcome a real visitor will routinely hit - not a rare race between two writers.
/// Modelling three ordinary outcomes as three different exception types would abuse exceptions for
/// control flow the caller must handle every time; a return value is the honest shape.</para>
/// </summary>
public sealed class PendingPhoneVerification
{
    public PendingPhoneVerificationId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>The <see cref="Visitor"/> the resulting <see cref="ChannelIdentity"/> will be linked to
    /// once confirmed - resolved once, at issue time, from the conversation the request was made in.
    /// Never re-resolved at confirmation time, the identical "always points at the same visitor from
    /// creation to consumption" shape <see cref="PendingChannelLinkRequest.VisitorId"/> already
    /// establishes.</summary>
    public VisitorId VisitorId { get; }

    /// <summary>Canonical E.164 form (<see cref="PhoneNumber"/>) - the one normalised string this item's
    /// own "where this is most likely to go wrong" note requires reused unchanged for the rate-limit key,
    /// this column, and the <see cref="ExternalChannelAddress"/> <see cref="ChannelIdentity.Link"/>
    /// eventually receives.</summary>
    public string Phone { get; } = string.Empty;

    public byte[] CodeHash { get; } = [];

    public PhoneVerificationDeliveryMethod DeliveryMethod { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Wrong-code guesses only - a correct confirmation never increments this, and expiry/lockout
    /// checks that refuse before ever comparing the code do not either (<see cref="AttemptConfirm"/>'s own
    /// remarks on why those are checked first).</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Application-supplied at issue time (`PhoneVerificationOptions`), not a Domain constant -
    /// the identical "duration is configuration, not compiled into Domain" split
    /// <see cref="PendingChannelLinkRequest.Request"/>'s own <c>validFor</c> parameter already
    /// establishes for <see cref="ExpiresAt"/>.</summary>
    public int MaxAttempts { get; }

    public bool IsLockedOut => AttemptCount >= MaxAttempts;

    public bool IsLive(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt && !IsLockedOut;

    private PendingPhoneVerification(
        PendingPhoneVerificationId id, SiteId siteId, VisitorId visitorId, string phone, byte[] codeHash,
        PhoneVerificationDeliveryMethod deliveryMethod, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        int maxAttempts, DateTimeOffset? consumedAt, int attemptCount)
    {
        Id = id;
        SiteId = siteId;
        VisitorId = visitorId;
        Phone = phone;
        CodeHash = codeHash;
        DeliveryMethod = deliveryMethod;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        MaxAttempts = maxAttempts;
        ConsumedAt = consumedAt;
        AttemptCount = attemptCount;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PendingPhoneVerification()
    {
    }

    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>
    /// Issues a fresh pending verification and raises <see cref="PhoneVerificationCodeIssued"/> - the
    /// event `Ago.Chat.Worker`'s own delivery consumer reacts to by actually placing the paid send. The
    /// caller (<c>InitiatePhoneVerificationHandler</c>) has already generated <paramref name="code"/> and
    /// hashed it into <paramref name="codeHash"/> - this factory never sees a code it did not receive
    /// pre-hashed for its own <see cref="CodeHash"/> column, but does receive the plaintext once, here, to
    /// carry it on the event - see <see cref="PhoneVerificationCodeIssued"/>'s own remarks for why that
    /// hop is unavoidable and how its exposure is bounded.
    /// </summary>
    public static PendingPhoneVerification Request(
        PendingPhoneVerificationId id, SiteId siteId, VisitorId visitorId, PhoneNumber phone, string code,
        byte[] codeHash, PhoneVerificationDeliveryMethod deliveryMethod, DateTimeOffset now, TimeSpan validFor,
        int maxAttempts)
    {
        var verification = new PendingPhoneVerification(
            id, siteId, visitorId, phone.Value, codeHash, deliveryMethod, now, now + validFor, maxAttempts,
            consumedAt: null, attemptCount: 0);

        verification._domainEvents.Add(new PhoneVerificationCodeIssued(
            id, siteId, phone.Value, code, deliveryMethod, now));

        return verification;
    }

    /// <summary>
    /// The one write path a wrong guess, an expired window, or a lockout all pass through - see this
    /// type's own remarks for why a return value, not an exception. Checked in the order that lets a
    /// caller who was never going to succeed find out as cheaply as possible: already consumed (a genuine
    /// race - two confirmations for the same row) first, then the window, then the lockout, and only then
    /// - the one comparison that costs anything - the code itself.
    ///
    /// <para>Constant-time comparison (<see cref="CryptographicOperations.FixedTimeEquals"/>), the same
    /// discipline every other hash-comparison in this codebase applying `never store a bearer-shaped value
    /// in plaintext` should also apply to <em>comparing</em> it - a variable-time compare would leak how
    /// many leading bytes matched through response timing, a real (if narrow, given the short code space)
    /// side channel this item's own honesty about "the hash buys no brute-force resistance" does not
    /// extend to condoning.</para>
    /// </summary>
    public PhoneVerificationConfirmOutcome AttemptConfirm(byte[] submittedCodeHash, DateTimeOffset now)
    {
        if (ConsumedAt is not null)
        {
            return PhoneVerificationConfirmOutcome.AlreadyConsumed;
        }

        if (now >= ExpiresAt)
        {
            return PhoneVerificationConfirmOutcome.Expired;
        }

        if (IsLockedOut)
        {
            return PhoneVerificationConfirmOutcome.LockedOut;
        }

        if (!CryptographicOperations.FixedTimeEquals(submittedCodeHash, CodeHash))
        {
            AttemptCount++;
            return IsLockedOut ? PhoneVerificationConfirmOutcome.LockedOut : PhoneVerificationConfirmOutcome.WrongCode;
        }

        ConsumedAt = now;
        return PhoneVerificationConfirmOutcome.Confirmed;
    }
}
