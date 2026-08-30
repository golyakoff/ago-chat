using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetModuleFlowReportForSite;

/// <summary>
/// `18-14`: the console's own chat-to-booking conversion block - <paramref name="From"/> inclusive,
/// <paramref name="To"/> exclusive, the same half-open convention
/// <see cref="Abstractions.IModuleFlowReadStore.GetSiteModuleFlowReportAsync"/> documents. Either or
/// both <see langword="null"/> means "let the handler default the window" -
/// <see cref="GetModuleFlowReportForSiteHandler.DefaultWindowDays"/>'s own remarks - the same
/// "the port takes the resulting timestamp, not a policy" split `18-08`'s own
/// <c>GetOperatorAnalyticsForSite</c> already establishes for an analogous bounded read. Carries no
/// module key: which module this report means is a deployment-wide configuration value
/// (<c>ModuleFlowReportOptions</c>), not a per-request choice - see that class's own remarks for why.
/// </summary>
public sealed record GetModuleFlowReportForSite(
    OperatorId RequestedBy, SiteId SiteId, DateTimeOffset? From, DateTimeOffset? To);
