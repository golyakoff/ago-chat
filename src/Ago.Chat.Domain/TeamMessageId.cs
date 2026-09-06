using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct TeamMessageId(Guid Value) : IStronglyTypedId;
