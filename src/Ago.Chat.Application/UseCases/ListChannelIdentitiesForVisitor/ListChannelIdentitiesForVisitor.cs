using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;

/// <summary>`14-12`: the console's own <c>VisitorPanel</c> listing - <paramref name="ConversationId"/> is
/// the conversation the operator is currently viewing, both the source of "which visitor" and the
/// per-conversation permission anchor, the identical shape `18-07`'s <c>GetVisitorHistory</c> already
/// establishes (see <see cref="ListChannelIdentitiesForVisitorHandler"/>'s own remarks).</summary>
public sealed record ListChannelIdentitiesForVisitor(ConversationId ConversationId, OperatorId RequestedBy, SiteId SiteId);
