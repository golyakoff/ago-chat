using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `14-14`: an operator, mid-conversation, writes down a phone number, email address, or other fact a
/// visitor just mentioned. <paramref name="ConversationId"/> is both the source of "which visitor" and
/// the tenant anchor - the identical shape <c>RequestChannelLinkFromConsole</c> already establishes for
/// itself (see <see cref="RecordVisitorContactDetailHandler"/>'s own remarks for why this reuses that
/// exact permission and lookup pattern rather than <c>ListChannelIdentitiesForVisitor</c>'s assigned-
/// operator check).
///
/// <para><paramref name="Kind"/> arrives as a raw string, not yet the validated
/// <see cref="VisitorContactDetailKind"/> - <c>RequestChannelLinkFromConsole.Kind</c>'s own precedent
/// for <see cref="ChannelKind"/>: the handler is what validates it, not the HTTP endpoint.</para>
/// </summary>
public sealed record RecordVisitorContactDetail(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, string Kind, string Value);
