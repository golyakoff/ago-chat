namespace Ago.Chat.Infrastructure.Vk;

/// <summary>`14-08`: thrown by <see cref="VkApiClient.GetGroupInfoAsync"/> when VK's own
/// <c>groups.getById</c> comes back a clear rejection (most commonly a bad or revoked token, or a token
/// missing the <c>groups</c> permission). Its caller, <c>Ago.Chat.Api</c>'s VK connect endpoint, catches
/// this specifically and refuses the connect attempt <em>before</em> ever writing a
/// <see cref="Domain.ChannelCredential"/> row - unlike <c>MaxSubscriptionRejectedException</c>'s own
/// caller, which must roll back a row it already created, this validation runs first (see
/// <c>VkChannelEndpoints</c>' own remarks for why VK's own shape allows that ordering), so there is
/// nothing to revoke.</summary>
public sealed class VkApiCallException(string message) : Exception(message);
