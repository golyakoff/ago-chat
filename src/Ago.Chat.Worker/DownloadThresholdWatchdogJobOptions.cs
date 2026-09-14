namespace Ago.Chat.Worker;

/// <summary>Bound from <c>DownloadThresholdWatchdogJob:*</c> config keys - the same `*JobOptions`
/// idiom every other Worker sweep in this file uses (<see cref="InactivityWatchdogJobOptions"/>'s own
/// remarks). Every value below is an implementer's-call default, not a measurement (CLAUDE.md bans
/// inventing a "typical" figure) - this job has no answered-decision number the way that job's own
/// <c>InactivityWindow</c>/<c>WarningLeadTime</c> do.</summary>
public sealed class DownloadThresholdWatchdogJobOptions
{
    public const string SectionName = "DownloadThresholdWatchdogJob";

    /// <summary>How often a sweep cycle runs. Ten minutes, not an hour - unlike
    /// <see cref="InactivityWatchdogJobOptions.Interval"/>'s own multi-day windows, a tenant crossing
    /// its own soft download threshold mid-workday wants to hear about it the same day, not up to an
    /// hour later; still far less often than a live request-path check, since this is a courtesy
    /// notice, not the hard-block enforcement itself (`GetAttachmentDownloadUrlHandler`'s own live,
    /// uncached read - CLAUDE.md rule 8 - is what actually gates a download instantly).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How many sites one sweep cycle claims - the same "external I/O per item, not a row"
    /// reasoning <see cref="InactivityWatchdogJobOptions.BatchSize"/> gives: a candidate claims one
    /// outbound SMTP send per operator on the site.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The Office console URL appended to the warning mail - the same "empty by default, a
    /// deployment that has not configured this yet must still start" shape
    /// <see cref="InactivityWatchdogJobOptions.ConsoleLoginUrl"/> already takes.</summary>
    public string ConsoleUrl { get; set; } = string.Empty;
}
