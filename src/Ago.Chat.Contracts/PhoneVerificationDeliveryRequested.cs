namespace Ago.Chat.Contracts;

/// <summary>
/// `14-15`: a pending phone verification was issued and needs its code delivered - published through the
/// outbox in the same transaction as the pending verification's own insert
/// (`Ago.Chat.Domain.PhoneVerificationCodeIssued`'s own remarks), consumed by `Ago.Chat.Worker`'s
/// `PhoneVerificationDeliveryConsumer` to actually place the paid SMS/voice send. CLAUDE.md rule 4's
/// "writes go through the outbox... never publish from inside a request handler" applies with extra force
/// here: the send is a real per-attempt cost and a real third-party network call, neither of which a
/// visitor-facing HTTP request may be made to wait on.
///
/// <para>Named <see cref="PhoneVerificationDeliveryRequested"/>, not the bare
/// <c>PhoneVerificationCodeIssued</c> the domain event already uses - the identical
/// <c>OperatorRemoved</c>/<c>OperatorRemovedFromSite</c> naming split for the identical reason: the
/// mapper that constructs this contract needs both types in scope at once, and a shared bare name would
/// be ambiguous to reference unqualified from inside it.</para>
///
/// <para><b>Carries <see cref="Code"/> in plaintext - the one integration event in this codebase that
/// does, and why that is a deliberate, bounded exception to `messaging.md`'s "payloads are small:
/// identifiers plus what a consumer cannot cheaply look up" rule rather than an oversight.</b> Every other
/// event on that page ships ids and lets a consumer re-read anything sensitive from Postgres at delivery
/// time - the identical pattern `BookingConfirmed`'s own remarks describe ("`20-05` looks the phone up
/// from `CustomerId` at send time"). That pattern cannot work here: the code is stored only as a one-way
/// SHA-256 hash (`PendingPhoneVerification.CodeHash`'s own remarks), by design, specifically so nothing -
/// including this consumer - can ever read it back out of Postgres. The plaintext has exactly one place
/// left to travel through on its way to the gateway, and this event is that hop. See
/// `Ago.Chat.Domain.PhoneVerificationCodeIssued`'s own remarks for how the resulting exposure window is
/// bounded (the outbox dispatcher's own poll-with-notify latency, `OutboxPruneJob`'s later cleanup, and
/// this row's own short `ExpiresAt`) rather than avoided outright.</para>
/// </summary>
public sealed record PhoneVerificationDeliveryRequested(
    Guid PendingPhoneVerificationId,
    Guid SiteId,
    string Phone,
    string Code,
    string DeliveryMethod,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
