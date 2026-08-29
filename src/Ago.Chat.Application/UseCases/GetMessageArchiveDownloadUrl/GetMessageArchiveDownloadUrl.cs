using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetMessageArchiveDownloadUrl;

public sealed record GetMessageArchiveDownloadUrl(
    SiteId SiteId, RetentionClass RetentionClass, DateOnly PeriodStart, OperatorId RequestedBy);
