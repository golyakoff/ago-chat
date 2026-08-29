using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ConversationNoteId(Guid Value) : IStronglyTypedId;
