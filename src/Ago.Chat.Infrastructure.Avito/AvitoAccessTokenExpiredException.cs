namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `14-11`: thrown by <see cref="AvitoApiClient.SendMessageAsync"/> specifically on HTTP 401 - Avito's
/// own access token genuinely expires every 24 hours (<c>AvitoDtos.cs</c>'s own citation), unlike every
/// other channel's durable bot/community token, so a 401 here is an <em>expected, routine</em> event
/// rather than the "credential is simply bad" case a 401 means for MAX/Telegram/VK. Kept as its own type,
/// distinct from <see cref="AvitoApiCallException"/>, so <see cref="AvitoChannelAdapter"/> can catch this
/// one case specifically and react by refreshing the token and retrying once, rather than surfacing an
/// ordinary <c>ChannelSendOutcome.Refused</c> the way a genuinely bad credential does
/// (<see cref="AvitoChannelAdapter.SendAsync"/>'s own remarks have the full reasoning).
/// </summary>
public sealed class AvitoAccessTokenExpiredException(string message) : Exception(message);
