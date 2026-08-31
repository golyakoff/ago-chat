namespace Ago.Chat.Domain;

/// <summary>
/// `14-15`: the answer <see cref="PendingPhoneVerification.AttemptConfirm"/> hands back, one member per
/// Done-when case this item's own backlog names ("a wrong code, an expired code, and a locked-out phone
/// ... are each refused, proven by a test per case"). A return value, not an exception - see that
/// method's own remarks for why this deliberately diverges from <see cref="PendingChannelLinkRequest.Consume"/>'s
/// throw-on-invalid-state shape.
/// </summary>
public enum PhoneVerificationConfirmOutcome
{
    Confirmed,
    WrongCode,
    Expired,
    LockedOut,
    AlreadyConsumed,
}
