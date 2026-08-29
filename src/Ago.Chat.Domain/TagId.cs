using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct TagId(Guid Value) : IStronglyTypedId;
