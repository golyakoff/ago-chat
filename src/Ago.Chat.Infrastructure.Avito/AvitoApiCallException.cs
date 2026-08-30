namespace Ago.Chat.Infrastructure.Avito;

/// <summary>`14-11`: thrown by <see cref="AvitoApiClient.GetSelfAsync"/> and
/// <see cref="AvitoApiClient.SubscribeWebhookAsync"/> when Avito comes back a clear rejection (most
/// commonly a bad, revoked or expired access token, or a token missing the required scope).
/// <c>Ago.Chat.Api</c>'s Avito connect endpoint catches this specifically and refuses the connect attempt
/// <em>before</em> ever writing a <see cref="Domain.ChannelCredential"/> row - <c>VkApiCallException</c>'s
/// own precedent for validating first rather than creating-then-rolling-back.</summary>
public sealed class AvitoApiCallException(string message) : Exception(message);
