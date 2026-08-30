using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;

/// <summary>`14-14`: an operator removes a mistaken or stale contact detail. Routed through the
/// conversation the operator is looking at, the same way <see cref="RecordVisitorContactDetail.RecordVisitorContactDetail"/>
/// is - see <see cref="DeleteVisitorContactDetailHandler"/>'s own remarks for why this, unlike `14-12`'s
/// <c>UnlinkChannelIdentity</c>, does not take a bare id plus a site-scoped route.</summary>
public sealed record DeleteVisitorContactDetail(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, VisitorContactDetailId ContactDetailId);
