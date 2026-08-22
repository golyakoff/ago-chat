namespace Ago.Chat.Worker;

/// <summary>Bound from <c>ConversationAssignmentJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).
///
/// Defaults are a starting point, not measured or load-tested (`CLAUDE.md`: "do not invent
/// numbers... measure or stay silent") - Stage 7 gives this a real number, same caveat already
/// attached to `MessageSendRateLimitOptions`/`DrainOptions`.</summary>
public sealed class ConversationAssignmentJobOptions
{
    public const string SectionName = "ConversationAssignmentJob";

    /// <summary>How often every site with waiting conversations gets a claim attempt. Short enough
    /// that a visitor is not left waiting long after an operator frees up, long enough not to hammer
    /// Postgres with an empty-queue poll between real events.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Waiting conversations claimed per site, per tick (`4-01`'s `WaitingConversationClaimQuery`).</summary>
    public int BatchSize { get; set; } = 20;
}
