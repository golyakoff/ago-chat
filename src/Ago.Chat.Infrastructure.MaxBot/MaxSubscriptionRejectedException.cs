namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>`14-02`: thrown by <see cref="MaxApiClient.SubscribeWebhookAsync"/> when MAX's own
/// <c>POST /subscriptions</c> comes back a clear rejection (most commonly a bad or revoked token). Its
/// caller, <c>Ago.Chat.Api</c>'s MAX registration endpoint, catches this specifically to revoke the
/// just-created <see cref="Domain.ChannelCredential"/> rather than leaving a known-bad credential in
/// place - see that endpoint's own remarks.</summary>
public sealed class MaxSubscriptionRejectedException(string message) : Exception(message);
