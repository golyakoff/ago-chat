namespace Ago.Chat.Domain;

/// <summary>`20-07`: whether a <see cref="ModuleTask"/> is still receiving the conversation's input.
/// Stored as the CLR member name via EF's default string conversion, matching <see
/// cref="ConversationState"/>/<see cref="AttachmentState"/>'s own precedent - see those types' own
/// remarks on why an ordinal would be a silent corruption risk.</summary>
public enum ModuleTaskState
{
    Open,
    Closed,
}
