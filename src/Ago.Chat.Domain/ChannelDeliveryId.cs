using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ChannelDeliveryId(Guid Value) : IStronglyTypedId;
