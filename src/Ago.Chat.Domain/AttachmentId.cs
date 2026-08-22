using Ago.Platform.Kernel;

namespace Ago.Chat.Domain;

public readonly record struct AttachmentId(Guid Value) : IStronglyTypedId;
