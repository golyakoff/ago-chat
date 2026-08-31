namespace Ago.Chat.Domain;

/// <summary>
/// `14-15`: raised by <see cref="PendingPhoneVerification.Request"/> - the fact that triggers
/// `Ago.Chat.Worker`'s own delivery consumer to actually place the paid SMS/voice send, mapped to an
/// outbox row in the same transaction as the pending verification's own insert (CLAUDE.md rule 4: state
/// change and integration event, one transaction - the identical shape <see cref="OperatorRemoved"/>'s
/// own remarks describe).
///
/// <para><b>Carries the plaintext <see cref="Code"/>, deliberately, unlike everything this aggregate
/// itself persists.</b> <see cref="PendingPhoneVerification.CodeHash"/> is the only form the code takes at
/// rest - this event is not "at rest", it is the one hop that must carry the real value, because the
/// Worker consumer that eventually dials or texts the phone has no way to reconstruct a value from a
/// one-way hash. The honest residual: the plaintext sits in `outbox.payload` from this transaction's
/// commit until the outbox dispatcher publishes it (normally milliseconds - the outbox is
/// poll-with-notify, not a batched sweep, `messaging.md`) and until <c>OutboxPruneJob</c> later removes
/// the published row, and in RabbitMQ's own queue until the delivery consumer acks it. This is the same
/// class of trade-off `PendingChannelLinkRequest`'s own remarks accept for its stored hash ("the real
/// security here rests on scope and the short expiry window, not on the hash being expensive to invert")
/// extended to a second, unavoidable hop - there is no channel-agnostic way to hand a Worker process a
/// value it must read in cleartext to dial a phone. Bounded by the identical short
/// <see cref="PendingPhoneVerification.ExpiresAt"/> window, not by keeping this hop secret.</para>
/// </summary>
public sealed record PhoneVerificationCodeIssued(
    PendingPhoneVerificationId PendingPhoneVerificationId,
    SiteId SiteId,
    string Phone,
    string Code,
    PhoneVerificationDeliveryMethod DeliveryMethod,
    DateTimeOffset OccurredAt) : IDomainEvent;
