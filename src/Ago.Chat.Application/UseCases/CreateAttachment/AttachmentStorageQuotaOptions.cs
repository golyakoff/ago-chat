namespace Ago.Chat.Application.UseCases.CreateAttachment;

/// <summary>
/// `23-76`: the tenant's own total attachment storage ceiling, by tier - bound from
/// <c>AttachmentStorageQuota:*</c> config keys, the same options-validation posture
/// (naming-and-structure.md) <see cref="AttachmentOptions"/> and <see cref="AttachmentRateLimitOptions"/>
/// already carry.
///
/// <para><b>The numbers come from the `ago-business` tier grid (2026-09-07), not this codebase.</b>
/// Free/Solo: 100 MiB total. Paid/Business: 1 GiB per paid year, <b>cumulative</b> - three paid years
/// is 3 GiB, not 1 GiB flat regardless of tenure. <see cref="SiteAttachmentQuotaPolicy"/> is the one
/// place that reading is applied; these two fields are only the per-unit prices it multiplies.</para>
///
/// <para><b>The "cumulative" reading is the tier-grid author's own interpretation, explicitly flagged
/// there as needing confirmation - not a measured or settled number.</b> Built as a named,
/// clearly-labelled config value for exactly that reason, the identical "starting point, not measured"
/// posture <see cref="AttachmentOptions"/>'s own remarks already state for its defaults (CLAUDE.md: "do
/// not invent numbers... measure or stay silent") - a one-line change in configuration, not a code
/// change, is what it costs if the author corrects the reading later.</para>
/// </summary>
public sealed class AttachmentStorageQuotaOptions
{
    public const string SectionName = "AttachmentStorageQuota";

    /// <summary>Free/Solo tier: a flat total, never growing with tenure - the tier grid's own words,
    /// "the free ceiling does not move." 100 MiB, the same order of magnitude
    /// <see cref="AttachmentOptions.MaxConversationBytes"/> already picks for one conversation's own
    /// budget, scaled up for "every conversation this tenant will ever have."</summary>
    public long FreeTierTotalBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Paid/Business tier: multiplied by the tenant's own count of paid years
    /// (<see cref="SiteAttachmentQuotaPolicy"/>), not applied flat - see this class's own remarks on
    /// why "cumulative" is the reading built here.</summary>
    public long PaidTierBytesPerPaidYear { get; set; } = 1024L * 1024 * 1024;
}
