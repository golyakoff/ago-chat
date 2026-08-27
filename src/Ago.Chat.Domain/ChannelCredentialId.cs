using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct ChannelCredentialId(Guid Value) : IStronglyTypedId;
