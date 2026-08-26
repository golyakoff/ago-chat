using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Application.UseCases.MintDemoTenant;

/// <summary>
/// Bound from <c>DemoTenant:*</c> (naming-and-structure.md's options convention), validated at
/// startup.
/// </summary>
public sealed class DemoTenantOptions
{
    public const string SectionName = "DemoTenant";

    /// <summary>
    /// <b>Off unless a deployment turns it on.</b> An endpoint that creates tenants from the public
    /// internet is not something a host should acquire by upgrading: only the demo deployment wants it,
    /// and a real customer's installation never does. `8-07`'s Out of scope is explicit that this must
    /// not become a second registration path, and a default of <see langword="false"/> is the cheapest
    /// enforcement of that.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How long a minted tenant lives. "Around a day" (`8-07`'s Goal) - long enough that
    /// somebody can come back to it after a night's sleep, short enough that a forgotten one is not a
    /// permanent row. Not a measured number.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The total cap: how many demo tenants may be alive at once, across every caller.
    ///
    /// <para>This is the half a per-IP rate limit cannot do. A limiter bounds one caller's rate; it
    /// does nothing about a thousand callers each politely minting one tenant, which is the shape an
    /// actual abuse of this endpoint takes. Treated as a correctness property with its own test
    /// (`MintDemoTenantHandlerTests`), not a number in a file nobody exercises.</para>
    ///
    /// <para>Fifty is a starting point, not a measurement - it is larger than any plausible number of
    /// simultaneous demo viewers this project will have and small enough that the row count stays
    /// legible in `12-03`'s owner view.</para>
    /// </summary>
    [Range(1, 10_000)]
    public int MaxLiveTenants { get; set; } = 50;

    /// <summary>
    /// The origin a minted tenant's widget may be embedded on - the public demo page. Written into the
    /// new site's <c>allowed_origins</c>, because a tenant whose origin list does not include the page
    /// the viewer is sent to is a tenant whose widget refuses to connect (`5-01`'s layer 2).
    ///
    /// <para>Configuration rather than a constant: the demo page's origin differs between the local
    /// loop and the deployment, and this repository holds no real endpoint (CLAUDE.md).</para>
    /// </summary>
    [Required]
    public string VisitorOrigin { get; set; } = "http://localhost:3000";
}

/// <summary>
/// The per-IP bucket for the minting endpoint. Its own options class rather than a reuse of
/// <c>RegisterSiteRateLimitOptions</c>: that one has a per-subject bucket, and an anonymous caller has
/// no subject. Same two-bucket reasoning does not apply here, so the shape honestly differs.
/// </summary>
public sealed class DemoTenantRateLimitOptions
{
    public const string SectionName = "DemoTenantRateLimit";

    /// <summary>Deliberately small. Minting is a once-per-visit action, and a caller who needs a third
    /// tenant in a minute is not demonstrating the product.</summary>
    public int PerIpCapacity { get; set; } = 3;

    public double PerIpRefillPerSecond { get; set; } = 3.0 / 3600; // ~3 an hour sustained
}
