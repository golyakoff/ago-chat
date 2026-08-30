using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct PendingChannelLinkRequestId(Guid Value) : IStronglyTypedId;
