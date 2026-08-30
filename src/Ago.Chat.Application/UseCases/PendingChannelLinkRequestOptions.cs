namespace Ago.Chat.Application.UseCases;

/// <summary>
/// `14-12`: bound from `PendingChannelLinkRequest:*` config keys - the same `3-05`/`RegisterSiteRateLimitOptions`
/// shape every options class in this codebase already uses. Shared by both originators
/// (<c>RequestChannelLinkFromConsoleHandler</c> and <c>HandleLinkIdentityCommandHandler</c>) rather than
/// living under either one's own folder - `adr/0079` decision 2 is explicit that the two produce the
/// identical row, so a single validity window for both is the honest shape, not two options classes that
/// could silently drift apart.
/// </summary>
public sealed class PendingChannelLinkRequestOptions
{
    public const string SectionName = "PendingChannelLinkRequest";

    /// <summary>15 minutes - long enough for a visitor to switch to another app, find the shop's bot,
    /// and send one message, short enough that a code left unused is not a standing, guessable bearer
    /// value for long (<see cref="Abstractions.IPendingChannelLinkCodeGenerator"/>'s own remarks on why
    /// this code is deliberately low-entropy). Not measured or load-tested - `CLAUDE.md`: "do not invent
    /// numbers... measure or stay silent" - the same honestly-stated-default shape
    /// <c>OperatorInviteOptions.ValidFor</c> already carries for its own, much longer window.</summary>
    public TimeSpan ValidFor { get; set; } = TimeSpan.FromMinutes(15);
}
