namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `24-11`: bound from <c>PersonExport:*</c> config keys - lives here, in Infrastructure, rather than
/// in <c>Ago.Chat.Application</c> the way `16-03`'s <c>SiteExportOptions</c>/`SiteExportJobOptions`
/// do, because <see cref="PersonExportArchiveWriter"/> is this value's only consumer and it is an
/// Infrastructure class - the same "a plain settings POCO lives beside the class that reads it" choice
/// this codebase already makes elsewhere for a value with exactly one reader.
/// </summary>
public sealed class PersonExportOptions
{
    public const string SectionName = "PersonExport";

    /// <summary>Lifetime of each presigned attachment-download URL embedded in the archive - the same
    /// seven-day SigV4 ceiling `SiteExportJobOptions.AttachmentUrlLifetime` uses, for the identical
    /// reason (`adr/0072`): not a measurement, the actual protocol ceiling. Its own field rather than a
    /// shared constant because the two live in different projects for the reasons
    /// <see cref="PersonExportArchiveWriter"/>'s own remarks give, not because the value should ever
    /// drift from its sibling.</summary>
    public TimeSpan AttachmentUrlLifetime { get; set; } = TimeSpan.FromDays(7);
}
