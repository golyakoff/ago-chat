using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ConversationId(Guid Value) : IStronglyTypedId;
