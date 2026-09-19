namespace Ago.Chat.Domain;

/// <summary>
/// `25-160`: the lifecycle of <see cref="Site"/>'s own uploaded logo object - see that aggregate's
/// <c>LogoObjectKey</c>/<c>LogoStatus</c>/<c>LogoRejectionReason</c> for the fields this drives. Closed,
/// four-member set, the same "SQL can enumerate it, so a CHECK constraint backstops the enum" shape
/// <see cref="Position"/>/<see cref="Locale"/> already establish for their own storage columns
/// (`SiteConfiguration`'s own check constraint for this column).
/// </summary>
public enum LogoStatus
{
    /// <summary>No logo has ever been uploaded - the default for every site, including every row that
    /// predates this column.</summary>
    None,

    /// <summary>Uploaded, staged under a private object key, and waiting for
    /// `Ago.Chat.Worker`'s validating consumer to decode and confirm it. Not yet servable by anything -
    /// <see cref="Site"/>'s own `LogoObjectKey` in this state names an object nothing outside the
    /// validating consumer should ever read.</summary>
    Pending,

    /// <summary>Decoded, confirmed a static PNG/JPEG/GIF at or under 100x100, and promoted to its
    /// permanent public object key - the one state <c>EmailChannelAdapter</c>/`TenantReplyEmailShell`
    /// ever renders a logo for.</summary>
    Ready,

    /// <summary>Failed validation (wrong format, wrong dimensions, or an animated GIF) - the tenant's
    /// previous <see cref="Ready"/> logo, if any, is untouched by this outcome (a rejected replacement
    /// never overwrites a working one - see `Site.RejectLogoUpload`'s own remarks).</summary>
    Rejected,
}
