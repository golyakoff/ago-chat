using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteAttachmentEgress;

/// <summary><see cref="PeriodMonth"/> is optional - a null query asks for the current calendar
/// month, computed from <see cref="Ago.Platform.Abstractions.IClock"/> inside the handler, never
/// trusted from a caller-supplied "today."</summary>
public sealed record GetSiteAttachmentEgress(SiteId SiteId, OperatorId RequestedBy, DateOnly? PeriodMonth);
