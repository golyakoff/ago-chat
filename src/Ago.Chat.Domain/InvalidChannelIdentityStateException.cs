namespace Ago.Chat.Domain;

/// <summary>`14-12`: thrown by <see cref="ChannelIdentity.Unlink"/> - <see
/// cref="InvalidChannelCredentialStateException"/>'s own precedent, one exception type per aggregate's own
/// invalid-transition guard.</summary>
public sealed class InvalidChannelIdentityStateException(string message) : Exception(message);
