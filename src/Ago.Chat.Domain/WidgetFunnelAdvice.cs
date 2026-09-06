namespace Ago.Chat.Domain;

/// <summary>
/// `23-07`: the one piece of advice a tenant's install screen leads with, chosen from the funnel's
/// three window counts by <see cref="WidgetFunnelAdviceResolver"/> - `docs/backlog/23-07-*.md`'s own
/// "The advice rule, which is the load-bearing half". Every member matches that section's own
/// four-way split (three zero-states plus "numbers are flowing, or `23-06`'s fourth state applies") so
/// a reviewer can check this enum against that prose without a translation step, the same discipline
/// <see cref="SiteInstallationState"/> already follows for its own four states.
/// </summary>
public enum WidgetFunnelAdvice
{
    /// <summary>Nothing to say: every stage of the funnel that has a predecessor with traffic also
    /// has traffic of its own, or <see cref="SiteInstallationState.NeverSeenButInUse"/> applies and
    /// install advice must not be produced regardless of the raw counts (the item's own words: "None
    /// of the above applies, and the install advice must not be produced").</summary>
    None,

    /// <summary>Zero loads in the window. "The script is not on the page, or its origin is refused" -
    /// recommending a channel here is actively harmful, the item's own example being a tenant sent
    /// off to connect Telegram instead of fixing an install that was never receiving a single visitor.
    /// </summary>
    FixInstall,

    /// <summary>Loads, but zero opens. Placement, appearance, first line - a channel will not help
    /// somebody who never sees the launcher, or sees it and does not click it.</summary>
    ImprovePlacement,

    /// <summary>Opens, but zero conversations. The visitor sees the panel and leaves without typing -
    /// *here* channels, offline auto-reply and response time are the right advice, per
    /// `docs/design/decisions.md` §3.</summary>
    ConnectChannelsAndRespond,
}
