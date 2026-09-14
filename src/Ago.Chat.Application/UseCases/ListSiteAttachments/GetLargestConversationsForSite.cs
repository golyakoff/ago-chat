using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListSiteAttachments;

public sealed record GetLargestConversationsForSite(SiteId SiteId, OperatorId RequestedBy, int? Limit);
