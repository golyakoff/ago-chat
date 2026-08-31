namespace Ago.Chat.Domain;

/// <summary>
/// `14-15`: how a verification code was delivered to the phone - SMS text, or a voice call reading the
/// digits aloud. Deliberately its own small enum, never folded into <see cref="ChannelKind"/>: this
/// describes an implementation/cost detail of <em>this item's own send</em>, not a routable identity.
/// <see cref="ChannelKind.Sms"/> is still what the resulting <see cref="ChannelIdentity"/> is tagged with
/// regardless of which member here delivered the code that proved it - see
/// <see cref="PendingPhoneVerification"/>'s own remarks on why one <see cref="ChannelKind"/> member is the
/// right amount of vocabulary for "this is a phone number", not two.
/// </summary>
public enum PhoneVerificationDeliveryMethod
{
    Sms,
    Voice,
}
