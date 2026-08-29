using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorAnalyticsForSite;

/// <summary>
/// `18-08`: the console's own "how am I doing" panel - <paramref name="From"/> inclusive,
/// <paramref name="To"/> exclusive, the same half-open convention
/// <see cref="Abstractions.IOperatorAnalyticsReadStore.GetSiteAnalyticsAsync"/> documents. Either or
/// both <see langword="null"/> means "let the handler default the window" -
/// <see cref="GetOperatorAnalyticsForSiteHandler.DefaultWindowDays"/>'s own remarks - the same
/// "the port takes the resulting timestamp, not a policy" split `18-01`'s <c>SearchConversations</c>
/// already establishes for an analogous bounded read.
/// </summary>
public sealed record GetOperatorAnalyticsForSite(
    OperatorId RequestedBy, SiteId SiteId, DateTimeOffset? From, DateTimeOffset? To);
