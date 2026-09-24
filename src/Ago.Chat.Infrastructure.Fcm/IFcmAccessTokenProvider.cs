namespace Ago.Chat.Infrastructure.Fcm;

/// <summary>
/// `26-100`: mints (and caches) the short-lived OAuth2 access token FCM HTTP v1 requires as its
/// <c>Authorization: Bearer</c>. A seam of its own - not folded into <see cref="FcmPushSender"/> - so the
/// sender can be tested against a fake token with no service account, private key or network, the same
/// reason <see cref="Ago.Chat.Application.Abstractions.IPushSender"/> exists at the layer above.
/// </summary>
public interface IFcmAccessTokenProvider
{
    /// <summary>Returns a currently-valid access token, minting a fresh one only when the cached token is
    /// absent or near expiry. Throws if the mint fails (no service account, a bad key, or Google's token
    /// endpoint refusing) - a credential-side failure the resilience pipeline treats as transient, never
    /// a per-device outcome.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
