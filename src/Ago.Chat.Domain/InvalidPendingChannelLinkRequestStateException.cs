namespace Ago.Chat.Domain;

/// <summary>`14-12`: thrown by <see cref="PendingChannelLinkRequest.Consume"/> - the same one-exception-
/// per-aggregate-invariant shape <see cref="InvalidChannelIdentityStateException"/>/
/// <see cref="InvalidOperatorInviteStateException"/> already use.</summary>
public sealed class InvalidPendingChannelLinkRequestStateException(string message) : Exception(message);
