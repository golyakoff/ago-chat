using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases;

/// <summary>
/// `14-15`: bound from `PhoneVerification:*` config keys, the same shape
/// `PendingChannelLinkRequestOptions` already establishes - shared by
/// <c>InitiatePhoneVerificationHandler</c> (which reads every field) and
/// <c>ConfirmPhoneVerificationHandler</c> (which reads none - it only ever reads the limits a
/// <em>previously</em> issued row already carries on itself, <see cref="PendingPhoneVerification.MaxAttempts"/>).
/// </summary>
public sealed class PhoneVerificationOptions
{
    public const string SectionName = "PhoneVerification";

    /// <summary>10 minutes - shorter than `PendingChannelLinkRequestOptions.ValidFor`'s 15, deliberately:
    /// this code is read off a phone call or an incoming SMS the visitor is looking at right now, not
    /// relayed by an operator across two apps, so there is less reason to hold the window open as long.
    /// Not measured or load-tested - the same honestly-stated-default caveat every `*Options` class in
    /// this codebase carries.</summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// `14-15`'s own Scope: "a bounded number of wrong-code attempts before the pending request is
    /// refused outright". 5, the same number `OperatorCapacityStore.ReleaseAsync`/`TransferConversationHandler`
    /// use for an unrelated retry budget - picked for the same reason stated there and nowhere else in
    /// this codebase for lockout specifically: not measured against a real attacker, a starting point
    /// deliberately generous enough that a visitor who fat-fingers a digit twice is not locked out by a
    /// third honest mistake, while still bounding a brute-force script's guesses against a six-digit space
    /// to a number small enough that guessing is not a realistic path to a false positive within the
    /// validity window.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// `14-15`'s own Open question 1, resolved here as a deployment-wide default (not yet per-site - no
    /// caller needs a per-site override today, and adding one before one exists would be the premature
    /// generalisation `clean-architecture.md` warns against). See this item's own commit-prep report for
    /// the full reasoning; in short, SMS is the default because a person reliably notices an SMS arriving
    /// (the author's own stated experience, named in the backlog item itself) and a one-time code that
    /// sits unread past its short `ValidFor` window is a failed verification, which matters more for this
    /// one-shot use case than the per-message cost difference from voice.
    /// </summary>
    public PhoneVerificationDeliveryMethod DefaultDeliveryMethod { get; set; } = PhoneVerificationDeliveryMethod.Sms;
}
