namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>
/// Bound from <c>RegisterSiteRateLimit:*</c> config keys - the same `3-05` shape every other rate
/// limit options class in this codebase already uses. `10-01`'s own Scope names this endpoint as the
/// real abuse surface this project's code must guard (Keycloak's hosted registration form sits
/// outside `Ago.Chat.Api`'s request path, so `IRateLimiter` cannot cover *that*), so both buckets are
/// deliberately conservative - a caller that legitimately needs more than a handful of registration
/// attempts per window does not exist yet.
///
/// Defaults are a starting point, not measured or load-tested, same caveat as every other rate-limit
/// options class here (`CLAUDE.md`: "measure or stay silent").
/// </summary>
public sealed class RegisterSiteRateLimitOptions
{
    public const string SectionName = "RegisterSiteRateLimit";

    /// <summary>Per Keycloak `sub` - mostly a defence against a retried request after a transient
    /// failure, not the real abuse boundary (a distinct `sub` per attempt is exactly what Keycloak's
    /// own registration form makes cheap to mint, `10-01`'s own Scope note on why the *IP* bucket is
    /// the one doing the real work here).</summary>
    public int PerSubjectCapacity { get; set; } = 3;

    public double PerSubjectRefillPerSecond { get; set; } = 3.0 / 3600;

    /// <summary>Per caller IP - the bucket that actually bounds "how many sites can one caller stand
    /// up," since minting a fresh Keycloak identity is cheap but a caller's IP is comparatively not.
    /// Deliberately coarser than the per-visitor buckets elsewhere in this codebase (`3-05`) - an IP
    /// can legitimately represent many distinct people behind NAT/a shared office connection.</summary>
    public int PerIpCapacity { get; set; } = 10;

    public double PerIpRefillPerSecond { get; set; } = 10.0 / 3600;
}
