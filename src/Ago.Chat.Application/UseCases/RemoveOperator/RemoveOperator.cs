using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RemoveOperator;

/// <summary>`13-03`: "this person is gone" - a site's `Permission.SiteManageOperators` holder's own
/// call, the same gate `13-01`'s invite generation already uses.</summary>
public sealed record RemoveOperator(OperatorId RequestedBy, SiteId SiteId, OperatorId TargetOperatorId);
