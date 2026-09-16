namespace Ago.Chat.Contracts;

/// <summary>The realtime protocol's wire shape for `25-110`'s live push, pushed to the one visitor
/// connection holding the affected conversation open as `"AttachmentUploadGrantChanged"` - the same
/// pattern <see cref="ConversationAssignedDto"/> already establishes for a single-fact hub push
/// (api-design.md: "payload shapes live in Ago.Chat.Contracts").</summary>
public sealed record AttachmentUploadGrantChangedDto(Guid ConversationId, bool Granted, DateTimeOffset OccurredAt);
