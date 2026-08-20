using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct VisitorId(Guid Value) : IStronglyTypedId;
