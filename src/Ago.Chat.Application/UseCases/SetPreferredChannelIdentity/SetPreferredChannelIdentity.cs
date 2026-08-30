using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetPreferredChannelIdentity;

/// <summary>`14-13`/`adr/0079` decision 5: the console's own "mark this channel preferred" action -
/// <paramref name="ConversationId"/> is the conversation the operator is currently viewing, both the
/// source of "which visitor" and the per-conversation permission anchor, the identical shape `14-12`'s
/// <c>ListChannelIdentitiesForVisitor</c> already establishes (see
/// <see cref="SetPreferredChannelIdentityHandler"/>'s own remarks). <paramref name="ChannelIdentityId"/>
/// is <see langword="null"/> for the explicit "back to automatic" action - clearing a preference is not
/// a second use case, it is this one with no id named.</summary>
public sealed record SetPreferredChannelIdentity(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, ChannelIdentityId? ChannelIdentityId);
