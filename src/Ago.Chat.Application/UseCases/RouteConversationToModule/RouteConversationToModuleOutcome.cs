namespace Ago.Chat.Application.UseCases.RouteConversationToModule;

/// <summary>Every non-<see cref="ConversationErrors"/>-failure way <c>RouteConversationToModuleHandler</c>
/// can resolve one trigger message - the same "a skip is an ack, not a nack" shape
/// `SendOfflineAutoReply`'s own <c>OfflineAutoReplyOutcome</c> establishes for its consumer.</summary>
public enum RouteConversationToModuleOutcome
{
    /// <summary>The loop guard: not a visitor-authored message, so nothing here has any business
    /// deciding anything about it.</summary>
    NotAVisitorMessage,

    /// <summary>No active task, and the message's first token matched no site-enabled module's trigger
    /// word - ordinary conversation, the overwhelming majority of messages.</summary>
    NoTriggerMatch,

    /// <summary>A trigger matched, the module answered, and a new <see cref="Domain.ModuleTask"/> is
    /// now the conversation's active one.</summary>
    TaskStarted,

    /// <summary>A trigger matched but the module could not be reached before any task existed - no
    /// <see cref="Domain.ModuleTask"/> was ever started, so there is nothing to close; the visitor is
    /// simply told the entry point is unavailable right now.</summary>
    ModuleUnavailableAtTrigger,

    /// <summary>An active task's reply could not be resolved to a value (an out-of-range or
    /// non-numeric text-channel reply against a choice-shaped step) - the module was never called, and
    /// the task stays open exactly as it was.</summary>
    ReplyNotResolved,

    /// <summary>An active task's reply was resolved, submitted, and the module answered with more work
    /// left to do - the task's own step advanced.</summary>
    StepAdvanced,

    /// <summary>An active task's reply was resolved, submitted, and the module reported completion -
    /// the task is now closed.</summary>
    TaskCompleted,

    /// <summary>An active task's module could not be reached - the task was closed and the visitor was
    /// told a person will take over (backlog item's own escalation rule).</summary>
    Escalated,

    /// <summary>
    /// `20-09`: a reply against a <see cref="Domain.PrimitiveKinds.VerifiedPhoneForm"/> step named a
    /// phone number with no active, verified <c>ChannelIdentity</c> behind it for this visitor - the
    /// module is never called (the same "nothing to stage, nothing to save" shape
    /// <see cref="ReplyNotResolved"/> already has), and the task stays open exactly as it was, with one
    /// difference from that case: the visitor is told, by name, to verify the number first, since
    /// unlike an out-of-range choice this is not something retyping the same text differently would
    /// ever fix.
    /// </summary>
    PhoneVerificationRequired,

    /// <summary>`CLAUDE.md` rule 5: this exact trigger message was already processed by this consumer -
    /// a redelivery, correctly producing no second effect.</summary>
    AlreadyProcessed,
}
