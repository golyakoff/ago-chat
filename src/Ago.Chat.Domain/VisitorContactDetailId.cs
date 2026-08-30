using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct VisitorContactDetailId(Guid Value) : IStronglyTypedId;
