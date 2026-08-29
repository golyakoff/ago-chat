namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SubscriptionRenewalJob:*</c>. Neither number is measured; both are shapes
/// (`CLAUDE.md`: "do not invent... measure or stay silent").</summary>
public sealed class SubscriptionRenewalJobOptions
{
    public const string SectionName = "SubscriptionRenewalJob";

    /// <summary>An hour, not a day - `13-03`'s own "daily retries" is enforced by
    /// <c>BillingSubscription.IsRetryDue</c>'s own elapsed-time gate, not by this tick interval, so
    /// running more often than once a day costs nothing (a tick with nothing due does one indexed
    /// query and stops) and buys a renewal reacting within the hour it actually becomes due, rather
    /// than up to a day late.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Candidates processed per tick - the same bounded-batch shape every other sweep job in
    /// this codebase uses (`AutoCloseInactiveConversationsJobOptions.BatchSize`'s own remarks).</summary>
    public int BatchSize { get; set; } = 100;
}
