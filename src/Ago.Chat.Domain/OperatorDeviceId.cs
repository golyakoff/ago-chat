using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct OperatorDeviceId(Guid Value) : IStronglyTypedId;
