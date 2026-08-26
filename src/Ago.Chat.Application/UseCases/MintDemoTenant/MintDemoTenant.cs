namespace Ago.Chat.Application.UseCases.MintDemoTenant;

/// <summary>
/// `8-07`: everything the minting endpoint knows about its caller, which is one thing.
///
/// <para>There is no name, no email and no identity, because there is no account being created for a
/// person - `8-07` sidesteps `10-05` precisely because a minted demo account has no address to verify
/// and nobody to mail. <paramref name="RequestIp"/> is the rate-limit key and nothing else; it is
/// never stored (`personal-data.md` records the edge access log as the place client IPs live, and this
/// item adds no second one).</para>
/// </summary>
public sealed record MintDemoTenant(string RequestIp);

/// <summary>
/// What the viewer is shown. All of it is displayed on screen once and never mailed, so this is the
/// only moment <paramref name="Password"/> exists anywhere outside Keycloak - nothing in this system
/// stores it.
/// </summary>
/// <param name="VisitorUrl">Where to open the shop side, carrying this tenant's own public key. The
/// public demo pages read it and boot the widget against this tenant instead of the shared one -
/// without which a per-viewer tenant would be an empty console, which is worse than the shared account
/// it replaces (`adr/0058`).</param>
public sealed record MintedDemoTenant(
    string Username,
    string Password,
    string SiteName,
    string SitePublicKey,
    string VisitorUrl,
    DateTimeOffset ExpiresAt);
