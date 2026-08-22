namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorDisconnectGraceConsumer:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).
///
/// <see cref="GracePeriod"/> is a starting point, not measured or load-tested (`CLAUDE.md`: "do not
/// invent numbers... measure or stay silent") - Stage 7 gives this a real number, same caveat
/// already attached to `MessageSendRateLimitOptions`/`DrainOptions`/`ConversationAssignmentJobOptions`.</summary>
public sealed class OperatorDisconnectGraceConsumerOptions
{
    public const string SectionName = "OperatorDisconnectGraceConsumer";

    /// <summary>How long an operator may have zero live connections before their conversations are
    /// released back to the queue.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxAttempts { get; set; } = 5;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);
}
