using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`: what a tenant needs to put the widget on their own site - the key that identifies them to
/// it, and the origin(s) the widget's own browser-side check (`api-design.md`'s "layer 2") requires the
/// embedding page to match. <see cref="AllowedOrigins"/> is a list because
/// <see cref="Domain.Site.AllowedOrigins"/> already is one (multiple origins per site is a real,
/// existing shape - `10-06`'s own Out of scope only says *widening the console's own signup form* to
/// collect more than one is a separate decision, not that the domain or this read should pretend the
/// list is a single value).
///
/// <para><b>`23-06`: the six fields below.</b> The four raw facts
/// (<see cref="FirstSeenAt"/>/<see cref="LastSeenAt"/>/<see cref="LastRefusedOrigin"/>/
/// <see cref="LastRefusedOriginAt"/>), the second fact
/// (<see cref="UsedRecently"/> - "the product was used", read from `conversations`, nothing new
/// stored), and <see cref="State"/> - the one resolved reading of all of the above, computed here
/// (<see cref="SiteInstallationStateResolver"/>) so the console never re-derives the rule from the raw
/// facts itself. All four raw facts are still returned alongside it: the console needs
/// <see cref="FirstSeenAt"/>/<see cref="LastSeenAt"/> to word "installed, quiet for N days" and
/// <see cref="LastRefusedOrigin"/> to name the actual origin being refused, neither of which the
/// enum alone carries.</para>
/// </summary>
public sealed record SiteInstallationDto(
    string PublicKey,
    IReadOnlyList<string> AllowedOrigins,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    string? LastRefusedOrigin,
    DateTimeOffset? LastRefusedOriginAt,
    bool UsedRecently,
    SiteInstallationState State);
