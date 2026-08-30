using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>
/// <paramref name="VisitorId"/> is already decided by the caller (the host, `1-06`) - a freshly
/// minted id for a first-time visitor, or the one decoded from an existing signed token for a
/// returning one. This use case only decides what happens to the conversation.
///
/// <para>`18-12`: <paramref name="Source"/> is optional and defaults to <see langword="null"/> - every
/// caller before this item, and every existing test, constructs this record with no opinion about
/// traffic source at all. It is used only on the "genuinely new conversation" branch of
/// <see cref="StartConversationHandler"/> - a resumed or already-existing conversation ignores it
/// entirely, the same "captured once, never revisited" rule <see cref="Domain.Conversation.Source"/>
/// itself enforces.</para>
/// </summary>
public sealed record StartConversation(SiteId SiteId, VisitorId VisitorId, TrafficSource? Source = null);
