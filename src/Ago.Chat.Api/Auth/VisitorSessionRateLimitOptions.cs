namespace Ago.Chat.Api.Auth;

/// <summary>
/// Bound from <c>VisitorSessionRateLimit:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Per-site only - there is no visitor id yet
/// at handshake time, that is exactly what this endpoint is about to mint (`caching.md`'s Goal).
/// Lives here, not `Ago.Chat.Application`: nothing about `POST /api/v1/visitor-sessions` goes
/// through an Application handler today (`AuthEndpoints` composes `GetSiteConfigByPublicKeyHandler`,
/// `IIdGenerator`, and `JwtTokenService` directly), so this option has no natural Application home
/// to live next to - unlike `MessageSendRateLimitOptions`, which sits beside the handler that is its
/// only consumer.
///
/// Default is not measured or load-tested - a starting point, same caveat as
/// `MessageSendRateLimitOptions`.
/// </summary>
public sealed class VisitorSessionRateLimitOptions
{
    public const string SectionName = "VisitorSessionRateLimit";

    public int PerSiteCapacity { get; set; } = 20;

    public double PerSiteRefillPerSecond { get; set; } = 20.0 / 60; // ~20 handshakes/minute sustained
}
