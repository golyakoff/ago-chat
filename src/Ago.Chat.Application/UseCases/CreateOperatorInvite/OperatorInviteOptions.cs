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
    /// credential indefinitely.</summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromDays(7);
}
