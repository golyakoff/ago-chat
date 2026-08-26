using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ChannelIdentityId(Guid Value) : IStronglyTypedId;
