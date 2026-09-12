namespace Ago.Chat.Worker;

/// <summary>Bound from <c>InactivityWatchdogJob:*</c> config keys, the same `*JobOptions` idiom every
/// other Worker sweep in this file uses (`SiteErasureJobOptions`'s own remarks). Every value below is
/// an implementer's-call default, not a measurement (`CLAUDE.md` bans inventing a "typical" figure) -
/// except <see cref="InactivityWindow"/> and <see cref="WarningLeadTime"/>, which are not defaults at
/// all but the author's own answered decision (`23-73`'s own backlog item, "Answered, 2026-09-12":
/// three months, warned fifteen days ahead).</summary>
public sealed class InactivityWatchdogJobOptions
{
    public const string SectionName = "InactivityWatchdogJob";

    /// <summary>How often a sweep cycle runs. An hour, not a minute - this job's own windows are
    /// measured in days, so nothing is lost by polling far less often than
    /// <see cref="AttachmentOrphanSweepJobOptions.Interval"/>'s own attachment-lifetime sweep does; the
    /// same "no measured target, just an operational default" posture <see cref="DemoTenantExpiryJobOptions.Interval"/>'s
    /// own remarks take for a similarly slow-moving condition.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many sites one sweep cycle's warning pass, and separately its erasure pass, each
    /// claim - the same "external I/O per item, not a row" reasoning
    /// <see cref="SiteErasureJobOptions.BatchSize"/> gives: a warning claims one outbound SMTP send per
    /// operator on the site, and an erasure claims one <c>IErasureRequestRepository</c> round trip.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// The account-inactivity backlog item's own three months - the author's answered decision, not a
    /// default this job invented (`23-73`'s "Answered" section, verbatim: "если... нету в течение трёх
    /// месяцев аккаунт удаляется"). Expressed as a fixed day count, the same
    /// <c>BillingSubscription.PeriodLength</c>/<c>PastDueRetryWindow</c> precedent already sets for
    /// "a month"/"a week" in this codebase - calendar-exact month arithmetic is not this product's own
    /// convention anywhere else, so ninety days is the consistent choice here too, not a new one.
    /// </summary>
    public TimeSpan InactivityWindow { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// `N` in the backlog item's own "Answered" section - "warned by mail, `N` days ahead, `N = 15` in
    /// config." Config-bound exactly as that section demands, not hardcoded - this field is that
    /// config.
    /// </summary>
    public TimeSpan WarningLeadTime { get; set; } = TimeSpan.FromDays(15);

    /// <summary>
    /// The <c>{{loginUrl}}</c> placeholder in the drafted warning mail - this deployment's own Office
    /// console URL. Empty by default, the same "a deployment that has not configured this yet must
    /// still start" shape <see cref="EmailBotApiOptions.Domain"/>'s own remarks state for an unset
    /// deployment-wide address; an empty value substitutes as an empty string rather than refusing to
    /// send the mail at all; a site's operator still gets the mail's own two-hint text.
    /// </summary>
    public string ConsoleLoginUrl { get; set; } = string.Empty;
}
