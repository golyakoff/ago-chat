using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.UpdateSiteBranding;

/// <summary>`25-160`: the console's "Почта @" screen's company-name field write - a full replacement,
/// the same PUT-a-whole-value shape <c>UpdateWidgetConfig</c> already establishes for a single free-text
/// field.</summary>
public sealed record UpdateSiteBranding(SiteId SiteId, OperatorId RequestedBy, string? BrandCompanyName);
