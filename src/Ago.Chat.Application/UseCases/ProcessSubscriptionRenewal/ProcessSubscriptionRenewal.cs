using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal;

/// <summary>`13-03`: one candidate id from <c>IBillingSubscriptionRepository.ListDueForRenewalAsync</c> -
/// the recurring-charge job's own per-candidate command, one call per row per tick.</summary>
public sealed record ProcessSubscriptionRenewal(BillingSubscriptionId SubscriptionId);

/// <summary>What this pass actually did to <see cref="ProcessSubscriptionRenewal.SubscriptionId"/> - a
/// closed hierarchy so the job's own logging can report exactly what happened without re-deriving it
/// from the row.</summary>
public abstract record SubscriptionRenewalOutcome
{
    private SubscriptionRenewalOutcome()
    {
    }

    /// <summary>The row named by the command no longer exists, or was no longer due by the time this
    /// handler actually looked at it - a race between the candidate list and this call, or a second
    /// `Ago.Chat.Worker` replica that already processed it. A no-op, not an error.</summary>
    public sealed record NotDue : SubscriptionRenewalOutcome;

    /// <summary>Lapsed - the 7-day retry window closed, or a cancelled subscription reached its own
    /// period end. No charge was attempted.</summary>
    public sealed record Lapsed : SubscriptionRenewalOutcome;

    /// <summary>A renewal or retry charge succeeded; the row is (or remains) <c>Succeeded</c>.</summary>
    public sealed record Renewed : SubscriptionRenewalOutcome;

    /// <summary>A renewal or retry charge was refused by ЮKassa - the row is now (or remains)
    /// <c>PastDue</c>.</summary>
    public sealed record ChargeRefused(string Reason) : SubscriptionRenewalOutcome;
}
