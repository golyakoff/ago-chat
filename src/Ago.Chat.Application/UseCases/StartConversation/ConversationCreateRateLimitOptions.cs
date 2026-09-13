namespace Ago.Chat.Application.UseCases.StartConversation;

/// <summary>
/// `23-76`: bound from <c>ConversationCreateRateLimit:*</c> config keys, the same two-bucket shape
/// <c>MessageSendRateLimitOptions</c>/<c>AttachmentRateLimitOptions</c> already establish - "a rate
/// limit on conversation creation per origin and per address" (this item's own Scope, point 2), read
/// here as per-site ("origin": the tenant a fresh conversation is created against) and per-visitor
/// ("address": the identity that requests one).
///
/// <para><b>Per-visitor alone is a speed bump, not a limit, and this item says so about itself.</b> A
/// visitor identity is free to mint ("the design, and it should stay that way" - this item's own
/// words), so an attacker minting a fresh <c>VisitorId</c> per request starts a fresh bucket every
/// time; the per-visitor limit only slows an attacker reusing one identity. <c>PerSiteCapacity</c> is
/// the bucket that actually bounds the flood this item exists to stop, because a site cannot be
/// re-minted the way a visitor can - both are built, per the item's own literal text, but only one of
/// them is load-bearing against the threat model this item describes.</para>
///
/// <para>Deliberately not IP-address-based - this item's own "Blocking or rate-limiting by IP address"
/// section rejects that outright, in both directions (too coarse against shared VPN exits, too cheap
/// to evade for the one attacker it would target), and nothing here reopens it.</para>
///
/// Defaults are a starting point, not measured or load-tested, the same caveat every sibling options
/// class in this codebase already carries.
/// </summary>
public sealed class ConversationCreateRateLimitOptions
{
    public const string SectionName = "ConversationCreateRateLimit";

    public int PerVisitorCapacity { get; set; } = 5;

    public double PerVisitorRefillPerSecond { get; set; } = 5.0 / 3600; // ~5 new conversations/hour sustained

    public int PerSiteCapacity { get; set; } = 100;

    public double PerSiteRefillPerSecond { get; set; } = 100.0 / 3600; // ~100 new conversations/hour sustained
}
