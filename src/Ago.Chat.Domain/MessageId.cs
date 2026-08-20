using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct MessageId(Guid Value) : IStronglyTypedId;
