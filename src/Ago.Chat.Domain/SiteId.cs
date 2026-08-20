using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct SiteId(Guid Value) : IStronglyTypedId;
