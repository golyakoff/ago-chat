using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CancelSubscription;

/// <summary>`13-03`/`decisions/0006`: turns off auto-renewal - the paid tier keeps running until
/// `current_period_end`, no refund. <paramref name="RequestedBy"/> is proven against
/// `Permission.SiteConfigure`, the identical gate `13-02`'s checkout endpoint already uses for a
/// billing/tier decision.</summary>
public sealed record CancelSubscription(OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId SubscriptionId);
