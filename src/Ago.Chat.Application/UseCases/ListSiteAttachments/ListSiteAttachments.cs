using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListSiteAttachments;

public sealed record ListSiteAttachments(
    SiteId SiteId,
    OperatorId RequestedBy,
    AttachmentListSort Sort,
    AttachmentListFilterKind Filter,
    AttachmentListCursor? Cursor,
    int? Limit);
