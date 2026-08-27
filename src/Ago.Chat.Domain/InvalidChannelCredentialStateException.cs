namespace Ago.Chat.Domain;

/// <summary>`14-02`: thrown by <see cref="ChannelCredential.Revoke"/> - <see
/// cref="InvalidWebhookEndpointStateException"/>'s own precedent, one exception type per aggregate's own
/// invalid-transition guard.</summary>
public sealed class InvalidChannelCredentialStateException(string message) : Exception(message);
