namespace Ago.Chat.Application.UseCases.HasPendingOperatorInvite;

/// <summary>`25-73`: <paramref name="Email"/> is the caller's own validated token email, never a
/// caller-supplied value - <c>OperatorInviteEndpoints.HandleHasPendingInviteAsync</c> reads it off the
/// authenticated principal, the same "identity comes from the validated token" rule
/// <c>RedeemOperatorInvite</c>'s own remarks already state for this identical claim.</summary>
public sealed record HasPendingOperatorInvite(string Email);
