using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ConversationAssignmentId(Guid Value) : IStronglyTypedId;
