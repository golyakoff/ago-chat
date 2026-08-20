using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct OperatorId(Guid Value) : IStronglyTypedId;
