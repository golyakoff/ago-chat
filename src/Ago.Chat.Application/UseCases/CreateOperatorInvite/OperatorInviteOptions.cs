using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary>
/// Bound from `OperatorInvite:*` config keys - the same `3-05`/`RegisterSiteRateLimitOptions` shape
/// every options class in this codebase already uses. A single, hardcoded-by-default validity window,
/// not a per-site setting: `caching.md`'s rate-limit bucket defaults are this item's own precedent for
/// "hardcode a sane default, no per-site override yet" (backlog item's own Scope), and nothing here has
/// been measured or load-tested (`CLAUDE.md`: "do not invent numbers... measure or stay silent").
/// </summary>
public sealed class OperatorInviteOptions
{
    public const string SectionName = "OperatorInvite";

    /// <summary>Seven days - long enough that an invite shared over Slack or email (this item's own
    /// "out-of-band, copied and shared however the admin chooses") does not expire before a real person
    /// gets around to it, short enough that a stale, unredeemed invite does not stay a standing bearer
    /// credential indefinitely.
    ///
    /// <para>`25-73`: also the `lifespan` this item's own design hands Keycloak's own
    /// `execute-actions-email` call - "stated explicitly rather than left to Keycloak's own unrelated
    /// default, so the two expiries the previous design review flagged as able to silently disagree
    /// cannot" (the item's own point 2). One value, read once, never a second constant for the same
    /// duration.</para></summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>`25-73`: the operator console's own origin - `CreateOperatorInviteHandler` builds
    /// Keycloak's `execute-actions-email` `redirect_uri` from this plus `/callback?inviteCode=...`, so
    /// the browser lands back in the app already knowing which invite to resolve, on whatever device or
    /// browser actually opened the email link (replacing `23-27`'s `sessionStorage`-carried code, which
    /// cannot survive that). Its own Application-layer config value rather than a reuse of
    /// `Ago.Chat.Api.Cors.ConsoleOriginOptions.AllowedOrigins[0]` - that list exists for CORS/hub-origin
    /// comparison (possibly more than one entry, and it lives in `Ago.Chat.Api`, which Application
    /// cannot reference at all - `CLAUDE.md`'s dependency rule), not for "the one canonical URL to
    /// redirect an email link to". `[Required]`: an unset value would build a redirect_uri pointing
    /// nowhere, the identical "fail at boot, loudly" shape `KeycloakAdminOptions`/`ConsoleOriginOptions`
    /// already choose over silently minting a broken link.</summary>
    [Required(ErrorMessage = "OperatorInvite:ConsoleBaseUrl is required (25-73) - it is where the invite email's own link redirects to.")]
    public string ConsoleBaseUrl { get; set; } = string.Empty;
}
