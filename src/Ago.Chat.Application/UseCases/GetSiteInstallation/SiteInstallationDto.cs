namespace Ago.Chat.Application.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`: what a tenant needs to put the widget on their own site - the key that identifies them to
/// it, and the origin(s) the widget's own browser-side check (`api-design.md`'s "layer 2") requires the
/// embedding page to match. <see cref="AllowedOrigins"/> is a list because
/// <see cref="Domain.Site.AllowedOrigins"/> already is one (multiple origins per site is a real,
/// existing shape - `10-06`'s own Out of scope only says *widening the console's own signup form* to
/// collect more than one is a separate decision, not that the domain or this read should pretend the
/// list is a single value).
/// </summary>
public sealed record SiteInstallationDto(string PublicKey, IReadOnlyList<string> AllowedOrigins);
