namespace Ago.Chat.Domain;

/// <summary>
/// `20-07`/`adr/0065` decision 1: one <c>task</c> started inside a conversation - Chat holds an id and
/// whether it is open, nothing about what the module is doing. Constructed and mutated only through
/// <see cref="Conversation.StartModuleTask"/>/<see cref="Conversation.RecordModuleStep"/>/
/// <see cref="Conversation.CloseModuleTask"/>, the same "the aggregate root is the only place an
/// invariant is enforced" shape <see cref="Message"/>'s own remarks describe for itself.
///
/// <para><see cref="LastStepKind"/>/<see cref="LastStepActions"/> exist for exactly one reason: the
/// text-channel numeric-reply resolver (<see cref="ChoiceReplyTextResolver"/>) needs the *last recorded
/// step's* actions to resolve a bare number against, and nothing else in this system tracks "the most
/// recent set of choices offered in this conversation." <see cref="LastStepPayload"/> is carried
/// alongside for the same reason <c>PrimitiveTextRenderer</c> needs it - re-rendering the current step's
/// text (e.g. after an operator asks "what did the visitor last see?") needs the prompt, not only the
/// choices.</para>
/// </summary>
public sealed class ModuleTask
{
    public ModuleTaskId Id { get; }

    public ConversationId ConversationId { get; }

    public ModuleKey ModuleKey { get; }

    /// <summary>The module's own id for this task - opaque, never generated or interpreted by Chat
    /// (`adr/0065` decision 1: "Multi-step state belongs to the module, keyed by its own task id").</summary>
    public string ExternalTaskId { get; } = string.Empty;

    public ModuleTaskState State { get; private set; }

    public DateTimeOffset OpenedAt { get; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public MessageContentKind? LastStepKind { get; private set; }

    public MessagePayload? LastStepPayload { get; private set; }

    // Nullable, matching Message._actions' own shape exactly (rather than a non-nullable
    // default-empty list) - so Ago.Chat.Infrastructure.Postgres.Persistence.MessageContentConverters.Actions
    // (a ValueConverter<List<MessageAction>?, string?>) applies unchanged, with no second converter to
    // keep in sync with the first.
    private List<MessageAction>? _lastStepActions;

    public IReadOnlyList<MessageAction> LastStepActions => _lastStepActions ?? [];

    internal ModuleTask(
        ModuleTaskId id, ConversationId conversationId, ModuleKey moduleKey, string externalTaskId,
        DateTimeOffset now, MessageContentKind? stepKind, MessagePayload? stepPayload,
        IReadOnlyList<MessageAction> stepActions)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId))
        {
            throw new ArgumentException("A module task needs the module's own external task id.", nameof(externalTaskId));
        }

        Id = id;
        ConversationId = conversationId;
        ModuleKey = moduleKey;
        ExternalTaskId = externalTaskId;
        State = ModuleTaskState.Open;
        OpenedAt = now;
        LastStepKind = stepKind;
        LastStepPayload = stepPayload;
        _lastStepActions = stepActions.Count == 0 ? null : [.. stepActions];
    }

    // EF Core materialization only.
    private ModuleTask()
    {
    }

    internal void RecordStep(MessageContentKind kind, MessagePayload? payload, IReadOnlyList<MessageAction> actions)
    {
        if (State != ModuleTaskState.Open)
        {
            throw new InvalidOperationException($"Cannot record a step on module task {Id.Value}; it is already {State}.");
        }

        LastStepKind = kind;
        LastStepPayload = payload;
        _lastStepActions = actions.Count == 0 ? null : [.. actions];
    }

    internal void Close(DateTimeOffset now)
    {
        if (State == ModuleTaskState.Closed)
        {
            return;
        }

        State = ModuleTaskState.Closed;
        ClosedAt = now;
    }
}
