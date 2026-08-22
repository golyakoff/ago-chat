namespace Ago.Chat.Application.UseCases.SendMessage;

/// <summary>
/// Bound from <c>MessageSendRateLimit:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Two buckets, per `caching.md`'s Goal: a
/// misbehaving visitor should not exhaust a whole site's budget, and a site under abuse should not
/// let one visitor's traffic alone explain it.
///
/// Defaults are a starting point, not measured or load-tested (CLAUDE.md: "do not invent
/// numbers... measure or stay silent") - Stage 7 gives this a real number.
/// </summary>
public sealed class MessageSendRateLimitOptions
{
    public const string SectionName = "MessageSendRateLimit";

    public int PerVisitorCapacity { get; set; } = 10;

    public double PerVisitorRefillPerSecond { get; set; } = 10.0 / 60; // ~10 messages/minute sustained

    public int PerSiteCapacity { get; set; } = 100;

    public double PerSiteRefillPerSecond { get; set; } = 100.0 / 60; // ~100 messages/minute sustained
}
