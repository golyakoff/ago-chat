using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct OperatorInviteId(Guid Value) : IStronglyTypedId;
