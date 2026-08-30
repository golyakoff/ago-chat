namespace Ago.Chat.Infrastructure.WhatsApp;

/// <summary>`14-10`: thrown by <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/> when Meta's own Graph
/// API rejects the (token, phone_number_id) pair a connect attempt supplied - a bad or expired access
/// token, a token that lacks the <c>whatsapp_business_messaging</c> permission, or a phone number id the
/// token is not authorized for. Its caller, <c>Ago.Chat.Api</c>'s WhatsApp connect endpoint, catches this
/// specifically and refuses the connect attempt <em>before</em> ever writing a
/// <see cref="Domain.ChannelCredential"/> row - <c>WhatsAppChannelEndpoints</c>'s own remarks explain
/// why this ordering is possible here, the identical shape <c>VkApiCallException</c> already established
/// for VK's own equivalent check.</summary>
public sealed class WhatsAppApiCallException(string message) : Exception(message);
