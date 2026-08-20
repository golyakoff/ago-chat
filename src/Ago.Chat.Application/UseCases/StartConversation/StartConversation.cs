using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>
/// <paramref name="VisitorId"/> is already decided by the caller (the host, `1-06`) - a freshly
/// minted id for a first-time visitor, or the one decoded from an existing signed token for a
/// returning one. This use case only decides what happens to the conversation.
/// </summary>
public sealed record StartConversation(SiteId SiteId, VisitorId VisitorId);
