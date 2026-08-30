namespace Ago.Chat.Domain;

/// <summary>
/// `14-12`/`adr/0079`: a short-lived proof-of-control window - "site X believes visitor Y is about to
/// prove they also control some address on channel K, and here is the one code that proves it." Its own
/// aggregate, not a value object on <see cref="Visitor"/>, for the identical reasons
/// <see cref="OperatorInvite"/> is its own aggregate rather than a value object on <see cref="Site"/>:
/// it has its own lifecycle (generated, consumed once, or left to expire unconsumed) and its own
/// transaction boundary at generation time, independent of everything else the target <see cref="Visitor"/>
/// or the requesting <see cref="Operator"/> holds.
///
/// <para><b>Two symmetric originators, one shape (`adr/0079` decision 2).</b> <see cref="RequestedByOperatorId"/>
/// is <see langword="null"/> for a visitor-initiated request (the visitor typed <c>/linkidentity</c>
/// themselves) and set for a console-initiated one - the same row either way, consumed by the identical
/// confirmation branch in <c>ReceiveChannelMessageHandler</c>. Nothing downstream ever needs to ask which
/// path created a given row; the field exists only so a future audit view could show "who asked for
/// this", never as a branch condition anywhere in this item's own write path.</para>
///
/// <para><b><see cref="CodeHash"/>, not a reversible ciphertext - <see cref="OperatorInvite.CodeHash"/>'s
/// own precedent, not <see cref="ChannelCredential.TokenCiphertext"/>'s.</b> A pending link code is only
/// ever <em>compared</em> at confirmation time, never reproduced afterward - the same one-way-hash
/// reasoning `OperatorInvite`'s own remarks give. Unlike an invite code (256 bits, copy-pasted), this
/// code is deliberately <b>low</b> entropy - <see cref="IPendingChannelLinkCodeGenerator"/>'s own remarks
/// explain why a human must be able to type it into a chat window - so the hash buys no brute-force
/// resistance a determined attacker with the row in hand could not already get from trying every code in
/// the space directly. It is still hashed, for the same "never store a bearer-shaped value in plaintext"
/// discipline every other secret-shaped column in this codebase follows uniformly; the real security
/// here rests on scope (site + channel kind + target visitor, never a bare code alone) and the short
/// <see cref="ExpiresAt"/> window, not on the hash being expensive to invert.</para>
///
/// <para><b>Deliberately never globally unique.</b> Unlike <see cref="OperatorInvite.CodeHash"/> (a
/// single, global bearer credential, so a collision would let one invite redeem as another), two
/// different sites - or two different requests on the same site - can coincidentally mint the identical
/// code text with a small code space. <see cref="IPendingChannelLinkRequestRepository.FindLiveAsync"/>
/// always scopes its lookup to (site, channel kind, code hash) together, never the hash alone - the
/// mechanism `14-12`'s own cross-site-isolation Done-when leans on: a pending code for one site must
/// never match an inbound message on another site, even with the identical code value by coincidence.</para>
///
/// <para><b>No active expiry sweep (`14-12`'s own Out-of-scope, decided and recorded here).</b> An
/// expired or consumed row is simply excluded from <see cref="IsLive"/> at read time - it is never
/// matched again, but it is also never deleted by this item. Link requests are a low-frequency,
/// console-adjacent action (an operator or a visitor deliberately starting a linking flow), not a
/// per-message write like `messages` itself, so unbounded row growth here is a materially smaller and
/// slower problem than the ones this codebase already builds real prune jobs for
/// (`OutboxPruneJob`/`InboxPruneJob`/`WebhookDeliveryPruneJob`). A sweep is exactly the kind of thing to
/// add once real traffic says the table is actually growing in a way that matters, not before.</para>
/// </summary>
public sealed class PendingChannelLinkRequest
{
    public PendingChannelLinkRequestId Id { get; }

    public SiteId SiteId { get; }

    /// <summary>The <see cref="Visitor"/> this request will link a new <see cref="ChannelIdentity"/>
    /// onto, once confirmed - resolved once, at generation time, from the conversation the operator or
    /// visitor was already in. Never re-resolved at confirmation time, so a request always points at the
    /// same visitor from creation to consumption regardless of anything that happens to that
    /// conversation in between.</summary>
    public VisitorId VisitorId { get; }

    /// <summary>The channel this request expects the confirming address to arrive on - part of the
    /// match key alongside <see cref="SiteId"/>, so a code minted for a Telegram link can never be
    /// redeemed by a message arriving over VK.</summary>
    public ChannelKind Kind { get; }

    /// <summary>SHA-256 of the plaintext code shown to the requester exactly once, at generation - see
    /// this type's own remarks for why this is a hash and, unlike <see cref="OperatorInvite.CodeHash"/>,
    /// deliberately not a globally-unique one.</summary>
    public byte[] CodeHash { get; } = [];

    /// <summary><see langword="null"/> for a visitor-initiated request (`/linkidentity`); the requesting
    /// operator's id for a console-initiated one. See this type's own remarks on why nothing in this
    /// item's write path ever branches on this field.</summary>
    public OperatorId? RequestedByOperatorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Never matched again once either has happened - the single predicate
    /// <see cref="IPendingChannelLinkRequestRepository.FindLiveAsync"/>'s own query and
    /// <see cref="Consume"/>'s own guard both restate, kept here once so the two cannot drift apart.</summary>
    public bool IsLive(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    private PendingChannelLinkRequest(
        PendingChannelLinkRequestId id, SiteId siteId, VisitorId visitorId, ChannelKind kind, byte[] codeHash,
        OperatorId? requestedByOperatorId, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        DateTimeOffset? consumedAt)
    {
        Id = id;
        SiteId = siteId;
        VisitorId = visitorId;
        Kind = kind;
        CodeHash = codeHash;
        RequestedByOperatorId = requestedByOperatorId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        ConsumedAt = consumedAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private PendingChannelLinkRequest()
    {
    }

    public static PendingChannelLinkRequest Request(
        PendingChannelLinkRequestId id, SiteId siteId, VisitorId visitorId, ChannelKind kind, byte[] codeHash,
        OperatorId? requestedByOperatorId, DateTimeOffset now, TimeSpan validFor) =>
        new(id, siteId, visitorId, kind, codeHash, requestedByOperatorId, now, now + validFor, consumedAt: null);

    /// <summary>
    /// Marks this request consumed by a real confirmation - <see cref="ReceiveChannelMessage"/>'s new
    /// branch has already found this row via <see cref="IPendingChannelLinkRequestRepository.FindLiveAsync"/>
    /// (which only ever returns a live row), so reaching either guard below means a genuine race between
    /// two concurrent deliveries of the confirming message, the same "handler pre-checks, aggregate
    /// throws only on a race the pre-check could not close by itself" split
    /// <see cref="OperatorInvite.Redeem"/>'s own remarks describe.
    /// </summary>
    public void Consume(DateTimeOffset now)
    {
        if (ConsumedAt is not null)
        {
            throw new InvalidPendingChannelLinkRequestStateException(
                $"Pending channel link request {Id.Value} was already consumed.");
        }

        if (now >= ExpiresAt)
        {
            throw new InvalidPendingChannelLinkRequestStateException(
                $"Pending channel link request {Id.Value} has expired.");
        }

        ConsumedAt = now;
    }
}
