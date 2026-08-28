namespace Ago.Chat.Domain;

/// <summary>
/// `13-01`: an operator invite was asked to <see cref="OperatorInvite.Redeem"/> in a state that
/// cannot legally allow it - already redeemed, or past its own <see cref="OperatorInvite.ExpiresAt"/>.
/// Same "check state, then call, rather than let the domain method's own guard surface as an error"
/// shape <see cref="InvalidWebhookEndpointStateException"/>'s own remarks describe: by the time this
/// is reached, <c>OperatorInviteRedemptionRepository</c> has already re-checked both conditions itself
/// (the repository's own remarks explain why it does not trust its earlier, pre-transaction read of
/// the same two facts) - so this exception firing at all means either a genuine two-callers-racing
/// window that check did not close, or a caller other than the one production repository misusing this
/// aggregate directly. Either way, this method's own guard is what makes "an invite can never be
/// silently redeemed twice" true by construction rather than by every caller happening to check first.
/// </summary>
public sealed class InvalidOperatorInviteStateException(string message) : Exception(message);
